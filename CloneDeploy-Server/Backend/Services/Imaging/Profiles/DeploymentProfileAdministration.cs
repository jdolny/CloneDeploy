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
/// Manages deployment profile details and their creation, archiving, and deletion.
/// </summary>
public sealed class DeploymentProfileAdministration(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<DeploymentProfile>>> GetProfilesAsync(int imageId, string? search = null, bool includeDisabled = true)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileView);
        if (user is null)
            return ServiceResult<List<DeploymentProfile>>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var query = db.ImageProfiles.AsNoTracking().Where(x => db.Devices.Any(d => d.ImageId == imageId && d.DeploymentProfileId == x.Id));
        if (!includeDisabled)
            query = query.Where(x => x.Enabled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text) || (x.Description != null && x.Description.ToLower().Contains(text)));
        }

        return ServiceResult<List<DeploymentProfile>>.Ok(await query.OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<DeploymentProfile>>> GetAllProfilesAsync(string? search = null, bool includeDisabled = false)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileView);
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
                return ServiceResult<List<DeploymentProfile>>.Fail("Image.Forbidden");
            await using var catalog = await factory.CreateContextAsync();
            return ServiceResult<List<DeploymentProfile>>.Ok(await catalog.ImageProfiles.AsNoTracking()
                .Where(x => (includeDisabled || x.Enabled) && (search == null || x.Name.Contains(search)))
                .OrderBy(x => x.Name).Select(x => new DeploymentProfile { Id = x.Id, Name = x.Name, Enabled = x.Enabled }).ToListAsync());
        }

        await using var db = await factory.CreateContextAsync();
        var query = db.ImageProfiles.AsNoTracking().AsQueryable();
        if (!includeDisabled)
            query = query.Where(x => x.Enabled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text) ||
                                     (x.Description != null && x.Description.ToLower().Contains(text)));
        }

        return ServiceResult<List<DeploymentProfile>>.Ok(await query.OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<DeploymentProfile>> GetProfileByIdAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileView);
        if (user is null)
            return ServiceResult<DeploymentProfile>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var profile = await db.ImageProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return profile is null ? ServiceResult<DeploymentProfile>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.DeploymentProfileNotFound)) : ServiceResult<DeploymentProfile>.Ok(profile);
    }

    public async Task<ServiceResult<List<AuditLog>>> GetProfileAuditAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileView);
        if (user is null)
            return ServiceResult<List<AuditLog>>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<AuditLog>>.Ok(await db.AuditLogs.AsNoTracking()
            .Where(x => x.ObjectType == nameof(DeploymentProfile) && x.ObjectId == id)
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync());
    }

    public async Task<ServiceResult<DeploymentProfile>> GetProfileAsync(int imageId, int profileId)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileView);
        if (user is null)
            return ServiceResult<DeploymentProfile>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        var profile = await db.ImageProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId);
        return profile is null ? ServiceResult<DeploymentProfile>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.DeploymentProfileNotFound)) : ServiceResult<DeploymentProfile>.Ok(profile);
    }

    public Task<ServiceResult<DeploymentProfile>> CreateProfileAsync(int imageId, string name, string? description) => CreateProfileAsync(name, description);

    public async Task<ServiceResult<DeploymentProfile>> CreateProfileAsync(string name, string? description)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileEdit);
        if (user is null)
            return ServiceResult<DeploymentProfile>.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        name = name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult<DeploymentProfile>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));

        if (await db.ImageProfiles.AnyAsync(x => x.Name == name))
            return ServiceResult<DeploymentProfile>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ThisNameIsAlreadyInUse));

        var entity = new DeploymentProfile { Name = name, Description = description?.Trim() };
        db.ImageProfiles.Add(entity);
        await db.SaveChangesAsync();
        AddAudit(db, user, AuditType.Create, nameof(DeploymentProfile), entity.Id, entity.Name, new { entity.Name, entity.Description });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<DeploymentProfile>.Ok(entity);
    }

    public async Task<ServiceResult> SaveProfileGeneralAsync(DeploymentProfile input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.ImageProfiles.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ProfileNotFound));

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));

        if (await db.ImageProfiles.AnyAsync(x => x.Id != entity.Id && x.Name == name))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ThisNameIsAlreadyInUse));

        entity.Name = name;
        entity.Description = input.Description?.Trim();
        AddAudit(db, user, AuditType.Update, nameof(DeploymentProfile), entity.Id, entity.Name, new { Section = "General", entity.Name, entity.Description });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SaveProfileConfigurationAsync(DeploymentProfile input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.ImageProfiles.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ProfileNotFound));

        entity.Enabled = input.Enabled;
        entity.VisibleInWinPe = input.VisibleInWinPe;
        if (!System.Enum.IsDefined(input.OuPrompt)) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ChooseAnOUPromptMode));
        if (input.OuNamingRuleSetId is { } ruleId)
        {
            var rules = await db.OuNamingRuleSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ruleId && x.Enabled);
            if (rules is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ChooseAnEnabledOUNamingRuleSet));
            var error = OuNamingResolver.Validate(rules, await OuNamingResolver.CatalogAsync(db));
            if (error is not null) return ServiceResult.Fail("Image.Validation", error);
        }
        entity.OuNamingRuleSetId = input.OuNamingRuleSetId;
        entity.OuPrompt = input.OuPrompt;
        if (!System.Enum.IsDefined(input.AfterDeployment)) return ServiceResult.Fail("Image.Validation", UiText.Get("Deployment.AfterSuccessInvalid"));
        entity.AfterDeployment = input.AfterDeployment;
        entity.SetComputerName = input.SetComputerName;
        AddAudit(db, user, AuditType.Update, nameof(DeploymentProfile), entity.Id, entity.Name, new { Section = "Configuration", entity.Enabled, entity.VisibleInWinPe, entity.SetComputerName, entity.AfterDeployment, entity.OuNamingRuleSetId, entity.OuPrompt });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteProfileAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentProfileEdit);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.ImageProfiles.SingleOrDefaultAsync(x => x.Id == id);
        if (entity is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ProfileNotFound));

        if (await db.Devices.AnyAsync(x => x.DeploymentProfileId == id) || await db.DeviceGroups.AnyAsync(x => x.DeploymentProfileId == id))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ThisProfileIsAssignedToDevicesOrGroupsRemove));

        if (await db.Set<AutomaticDeploymentRule>().AnyAsync(x => x.DeploymentProfileId == id))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ThisProfileIsUsedByDeploymentRulesRemoveThose));

        if (await db.Set<ImagingDeployment>().AnyAsync(x => x.DeploymentProfileId == id))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileAdministration.ThisProfileIsReferencedByDeploymentHistoryDisableIt));

        AddAudit(db, user, AuditType.Delete, nameof(DeploymentProfile), entity.Id, entity.Name, new { entity.Name });
        db.ImageProfiles.Remove(entity);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
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
