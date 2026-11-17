using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages image details, settings, labels, and deletion.
/// </summary>
public sealed class ImageAdministration(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<Image>>> GetImagesAsync(string? search = null, bool includeDisabled = false)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageView);
        if (user is null)
        {
            var principal = (await authentication.GetAuthenticationStateAsync()).User;
            if (!(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceView)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceEdit)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceDeployImage)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceGroupView)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceGroupEdit)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceGroupDeployImage)).Succeeded &&
                !(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceGroupDeployMulticast)).Succeeded)
                return ServiceResult<List<Image>>.Fail("Image.Forbidden");
            await using var catalog = await factory.CreateContextAsync();
            return ServiceResult<List<Image>>.Ok(await catalog.Images.AsNoTracking()
                .Where(x => (includeDisabled || x.Enabled) && (search == null || x.Name.Contains(search)))
                .OrderBy(x => x.Name).Select(x => new Image { Id = x.Id, Name = x.Name, Enabled = x.Enabled, Format = x.Format }).ToListAsync());
        }

        await using var db = await factory.CreateContextAsync();
        var query = db.Images.AsNoTracking().Include(x => x.Devices).Include(x => x.Tags).AsSplitQuery().AsQueryable();
        if (!includeDisabled)
            query = query.Where(x => x.Enabled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text) || (x.Description != null && x.Description.ToLower().Contains(text)));
        }

        return ServiceResult<List<Image>>.Ok(await query.OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<Image>> GetImageAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageView);
        if (user is null)
            return ServiceResult<Image>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var image = await db.Images.AsNoTracking().Include(x => x.Tags).ThenInclude(x => x.Tag).SingleOrDefaultAsync(x => x.Id == id);
        return image is null ? ServiceResult<Image>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound)) : ServiceResult<Image>.Ok(image);
    }

    public async Task<ServiceResult<List<Tag>>> GetTagsAsync()
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.TagView);
        if (user is null)
            return ServiceResult<List<Tag>>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<Tag>>.Ok(await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<Image>> CreateImageAsync(string name, string? description, ImageFormat format = ImageFormat.Wim)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageCreate);
        if (user is null)
            return ServiceResult<Image>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        name = name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult<Image>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));

        if (await db.Images.AnyAsync(x => x.Name == name))
            return ServiceResult<Image>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ThisNameIsAlreadyInUse));

        if (!System.Enum.IsDefined(format)) return ServiceResult<Image>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ChooseWIMOrFFU));
        var entity = new Image { Name = name, Description = description?.Trim(), Format = format };
        db.Images.Add(entity);
        await db.SaveChangesAsync();
        AddAudit(db, user, AuditType.Create, nameof(Image), entity.Id, entity.Name, new { entity.Name, entity.Description, entity.Format });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<Image>.Ok(entity);
    }

    public async Task<ServiceResult> SaveImageGeneralAsync(Image input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.Images.Include(x => x.Tags).SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));

        if (await db.Images.AnyAsync(x => x.Id != entity.Id && x.Name == name))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ThisNameIsAlreadyInUse));

        var tagIds = input.Tags.Select(x => x.TagId).Distinct().ToList();
        if (!System.Enum.IsDefined(input.Format)) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ChooseWIMOrFFU));
        if (entity.Format != input.Format && (await db.ImageUploads.AnyAsync(x => x.ImageId == input.Id) || await db.ImageVersions.AnyAsync(x => x.ImageId == input.Id)))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.AnImageWithCapturesCannotChangeTypeCreateA));
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Count)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeviceGroupAdministration.OneOrMoreSelectedTagsNoLongerExist));

        entity.Name = name;
        entity.Description = input.Description?.Trim();
        entity.Format = input.Format;
        entity.Enabled = input.Enabled;
        entity.Protected = input.Protected;
        db.ImageTags.RemoveRange(entity.Tags.Where(x => !tagIds.Contains(x.TagId)));
        var existing = entity.Tags.Select(x => x.TagId).ToHashSet();
        foreach (var tagId in tagIds.Except(existing))
            entity.Tags.Add(new ImageTag { TagId = tagId });

        AddAudit(db, user, AuditType.Update, nameof(Image), entity.Id, entity.Name, new { Section = "General", entity.Name, entity.Description, entity.Format, entity.Enabled, entity.Protected, TagIds = tagIds });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SaveImageConfigurationAsync(Image input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.Images.Include(x => x.Tags).SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));

        entity.Enabled = input.Enabled;
        entity.Protected = input.Protected;
        AddAudit(db, user, AuditType.Update, nameof(Image), entity.Id, entity.Name, new { Section = "Configuration", entity.Enabled, entity.Protected });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteImageAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var entity = (await db.Images.FromSqlInterpolated($"SELECT * FROM \"Images\" WHERE \"Id\" = {id} FOR UPDATE").ToListAsync()).SingleOrDefault();
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));

        var versions = await db.ImageVersions.Where(x => x.ImageId == id).ToListAsync();
        var versionIds = versions.Select(x => x.Id).ToArray();
        await ImageDeletionPreparation.CancelReferencesAsync(db, versionIds);
        await db.Devices.Where(x => x.ImageId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ImageId, (int?)null));
        await db.DeviceGroups.Where(x => x.ImageId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ImageId, (int?)null));
        await db.Set<AutomaticDeploymentRule>().Where(x => x.ImageId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ImageId, (int?)null).SetProperty(x => x.Enabled, false));
        var uploads = await db.ImageUploads.AsNoTracking().Where(x => x.ImageId == id).ToListAsync();
        var uploadIds = uploads.Select(x => x.Id).ToArray();
        await ImageDeletionPreparation.CancelUploadsAsync(db, uploadIds);
        var content = versions.Select(x => new ImageContentDeletion { Id = x.Id, ImageId = id, StorageProviderId = x.StorageProviderId })
            .Concat(uploads.Select(x => new ImageContentDeletion { Id = x.Id, ImageId = id, StorageProviderId = x.StorageProviderId }))
            .DistinctBy(x => x.Id).ToList();
        var queued = await db.Set<ImageContentDeletion>().Where(x => x.ImageId == id).Select(x => x.Id).ToListAsync();
        db.Set<ImageContentDeletion>().AddRange(content.Where(x => !queued.Contains(x.Id)));
        await db.ImageUploads.Where(x => x.ImageId == id).ExecuteDeleteAsync();
        db.ImageVersions.RemoveRange(versions);
        AddAudit(db, user, AuditType.Delete, nameof(Image), entity.Id, entity.Name, new { entity.Name, DeletedVersions = versionIds });
        db.Images.Remove(entity);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteImagesAsync(IEnumerable<int> ids)
    {
        foreach (var id in ids.Distinct())
        {
            var result = await DeleteImageAsync(id);
            if (!result.Success)
                return result;
        }

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<AuditLog>>> GetAuditAsync(int imageId, int? profileId = null)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, profileId.HasValue ? Constants.Permissions.DeploymentProfileView : Constants.Permissions.ImageView);
        if (user is null)
            return ServiceResult<List<AuditLog>>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var type = profileId.HasValue ? nameof(DeploymentProfile) : nameof(Image);
        var id = profileId ?? imageId;
        if (profileId.HasValue && !await db.ImageProfiles.AnyAsync(x => x.Id == id))
            return ServiceResult<List<AuditLog>>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.DeploymentProfileNotFound));

        return ServiceResult<List<AuditLog>>.Ok(await db.AuditLogs.AsNoTracking().Where(x => x.ObjectType == type && x.ObjectId == id)
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync());
    }

    private static void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, AuditType type, string objectType, int id, string name, object detail)
    {
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity?.Name ?? "Administrator",
            AuditType = type,
            ObjectType = objectType,
            ObjectId = id,
            ObjectName = name,
            ObjectJson = JsonSerializer.Serialize(detail)
        });
    }
}
