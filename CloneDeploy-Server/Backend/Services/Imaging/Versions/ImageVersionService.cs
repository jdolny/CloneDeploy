using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Tracks image versions, completed uploads, and which version is selected for use.
/// </summary>
public sealed class ImageVersionService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<ImageVersion>>> GetVersionsAsync(int imageId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageView);
        if (user is null)
            return ServiceResult<List<ImageVersion>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await db.Images.AnyAsync(x => x.Id == imageId))
            return ServiceResult<List<ImageVersion>>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var versions = await db.ImageVersions.AsNoTracking()
            .Where(x => x.ImageId == imageId).OrderByDescending(x => x.Number).ToListAsync();
        var providerIds = versions.Select(x => x.StorageProviderId).Distinct().ToArray();
        var providers = await db.StorageProviders.AsNoTracking().Where(x => providerIds.Contains(x.Id)).Select(x => new StorageProvider { Id = x.Id, Name = x.Name }).ToDictionaryAsync(x => x.Id);
        foreach (var version in versions)
            version.StorageProvider = providers.GetValueOrDefault(version.StorageProviderId);
        return ServiceResult<List<ImageVersion>>.Ok(versions);
    }

    // Call only after the upload has been verified and finalized in this version's folder.
    // The future imaging endpoint must supply its own device/user authorization boundary.
    public async Task<ServiceResult<ImageVersion>> RegisterCompletedUploadAsync(int imageId, Guid uploadId, int storageProviderId, long sizeBytes)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageCapture);
        if (user is null)
            return ServiceResult<ImageVersion>.Fail("Image.Forbidden");
        if (uploadId == Guid.Empty || sizeBytes < 0)
            return ServiceResult<ImageVersion>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.InvalidUploadDetails));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        if (await db.Set<ImageContentDeletion>().AnyAsync(x => x.Id == uploadId))
            return ServiceResult<ImageVersion>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ThisUploadIsBeingDeleted));
        var image = await LockImageAsync(db, imageId);
        if (image is null)
            return ServiceResult<ImageVersion>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var existing = await db.ImageVersions.SingleOrDefaultAsync(x => x.Id == uploadId);
        if (existing is not null)
        {
            if (existing.ImageId != imageId || existing.StorageProviderId != storageProviderId || existing.SizeBytes != sizeBytes)
                return ServiceResult<ImageVersion>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ThisUploadWasAlreadyRegisteredWithDifferentDetails));
            return ServiceResult<ImageVersion>.Ok(existing);
        }

        if (!await db.StorageProviders.AnyAsync(x => x.Id == storageProviderId && x.IsEnabled))
            return ServiceResult<ImageVersion>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.SelectAnEnabledStorageProvider));
        var number = (await db.ImageVersions.Where(x => x.ImageId == imageId).MaxAsync(x => (int?)x.Number) ?? 0) + 1;
        var version = new ImageVersion
        {
            Id = uploadId, ImageId = imageId, Number = number, Name = $"Version {number}",
            UploadedAtUtc = DateTime.UtcNow, SizeBytes = sizeBytes, StorageProviderId = storageProviderId,
            Format = image.Format,
            IsActive = !await db.ImageVersions.AnyAsync(x => x.ImageId == imageId && x.IsActive)
        };
        version.ReplicationApproved = false;
        db.ImageVersions.Add(version);
        image.LastUploadGuid = uploadId;
        Audit(db, user, image, new { Section = "Versions", Operation = "Upload", version.Id, version.Number, version.IsActive });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<ImageVersion>.Ok(version);
    }

    public async Task<ServiceResult> ApproveAsync(int imageId, Guid versionId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageApprove);
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var image = await LockImageAsync(db, imageId);
        if (image is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == imageId && x.Id == versionId);
        if (version is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ImageVersionNotFound));
        if (version.IsApproved) return ServiceResult.Ok();
        version.IsApproved = true;
        version.ReplicationApproved = true;
        Audit(db, user, image, new { Section = "Versions", Operation = "Approve", version.Id, version.Number });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SetActiveAsync(int imageId, Guid versionId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var image = await LockImageAsync(db, imageId);
        if (image is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == imageId && x.Id == versionId);
        if (version is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ImageVersionNotFound));
        if (version.IsActive)
            return ServiceResult.Ok();
        if (!await db.StorageProviders.AnyAsync(x => x.Id == version.StorageProviderId && x.IsEnabled))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.EnableThisVersionSStorageProviderBeforeMakingIt));
        var previous = await db.ImageVersions.Where(x => x.ImageId == imageId && x.IsActive).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
        await db.ImageVersions.Where(x => x.ImageId == imageId && x.IsActive).ExecuteUpdateAsync(x => x.SetProperty(v => v.IsActive, false));
        version.IsActive = true;
        version.ReplicationApproved = version.IsApproved;
        Audit(db, user, image, new { Section = "Versions", Operation = "SetActive", PreviousVersionId = previous, VersionId = versionId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(int imageId, Guid versionId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var image = await LockImageAsync(db, imageId);
        if (image is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.Id == versionId && x.ImageId == imageId);
        if (version is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ImageVersionNotFound));
        await ImageDeletionPreparation.CancelReferencesAsync(db, [versionId]);
        db.Set<ImageContentDeletion>().Add(new() { Id = version.Id, ImageId = imageId, StorageProviderId = version.StorageProviderId });
        await db.ImageUploads.Where(x => x.Id == versionId).ExecuteDeleteAsync();
        db.ImageVersions.Remove(version);
        if (image.LastUploadGuid == versionId)
            image.LastUploadGuid = await db.ImageVersions.Where(x => x.ImageId == imageId && x.Id != versionId)
                .OrderByDescending(x => x.Number).Select(x => x.Id).FirstOrDefaultAsync();
        Audit(db, user, image, new { Section = "Versions", Operation = "Delete", version.Id, version.Name, version.IsActive });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SaveDetailsAsync(int imageId, Guid versionId, string name, string? description)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");
        name = name?.Trim() ?? "";
        description = description?.Trim();
        if (name.Length is < 1 or > 200 || description?.Length > 2000)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.EnterANameBetween1And200CharactersAnd));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var image = await LockImageAsync(db, imageId);
        if (image is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == imageId && x.Id == versionId);
        if (version is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageVersionService.ImageVersionNotFound));
        version.Name = name;
        version.Description = description;
        Audit(db, user, image, new { Section = "Versions", Operation = "Details", version.Id, version.Name, version.Description });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static async Task<Image?> LockImageAsync(CloneDeployDbContext db, int imageId) =>
        (await db.Images.FromSqlInterpolated($"SELECT * FROM \"Images\" WHERE \"Id\" = {imageId} FOR UPDATE").ToListAsync())
        .SingleOrDefault();

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, Image image, object detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update, ObjectType = nameof(Image),
            ObjectId = image.Id, ObjectName = image.Name, ObjectJson = JsonSerializer.Serialize(detail)
        });

}
