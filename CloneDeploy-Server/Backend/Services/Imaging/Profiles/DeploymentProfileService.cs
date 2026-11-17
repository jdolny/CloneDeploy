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

// Manages the reusable parts of a deployment and the defaults selected on each device.
/// <summary>
/// Manages the drivers, policies, and Windows setup choices used by deployment profiles.
/// </summary>
public sealed class DeploymentProfileService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public Task<ServiceResult<List<DomainJoinRuleChoice>>> GetOuRuleChoicesAsync() => ReadWithPermission(Constants.Permissions.DeploymentProfileView,
        db => db.OuNamingRuleSets.AsNoTracking().OrderBy(x => x.Name).Select(x => new DomainJoinRuleChoice(x.Id, x.Name, x.DomainName, x.Enabled)).ToListAsync());

    public Task<ServiceResult<List<DeploymentDriverRule>>> GetDriverRulesAsync(int id) => ReadWithPermission(Constants.Permissions.DeploymentProfileView, db => db.DeploymentDriverRules.AsNoTracking()
        .Where(x => x.DeploymentProfileId == id).Include(x => x.DriverPack).ThenInclude(x => x!.Models)
        .Include(x => x.DriverPack).ThenInclude(x => x!.RegexRules).AsSplitQuery().OrderBy(x => x.Id).ToListAsync());

    public Task<ServiceResult> SaveDriversAsync(DeploymentProfile input, IReadOnlyList<DeploymentDriverRule> rules) =>
        Write(nameof(DeploymentProfile), input.Id, "Drivers", async db =>
        {
            var profile = await db.ImageProfiles.SingleOrDefaultAsync(x => x.Id == input.Id);
            if (profile is null)
                return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.DeploymentProfileNotFound));
            if (!System.Enum.IsDefined(input.DriverMode))
                return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.ChooseADriverSelectionMode));
            profile.DriverMode = input.DriverMode;
            profile.DriverPackId = input.DriverMode == DeploymentDriverMode.Specific ? input.DriverPackId : null;
            await db.DeploymentDriverRules.Where(x => x.DeploymentProfileId == input.Id).ExecuteDeleteAsync();
            return ServiceResult.Ok();
        });

    public static string? GetDriverRuleValidationError(DeploymentDriverRule rule)
    {
        var exact = !string.IsNullOrWhiteSpace(rule.Manufacturer) || !string.IsNullOrWhiteSpace(rule.Model);
        var regex = !string.IsNullOrWhiteSpace(rule.ManufacturerRegex) || !string.IsNullOrWhiteSpace(rule.ModelRegex);
        if (exact == regex)
            return "Use either an exact manufacturer and model or regular expressions for each rule.";
        if (exact && (string.IsNullOrWhiteSpace(rule.Manufacturer) || string.IsNullOrWhiteSpace(rule.Model)))
            return "Enter both manufacturer and model.";
        if (new[] { rule.Manufacturer, rule.Model, rule.ManufacturerRegex, rule.ModelRegex }.Any(x => x?.Length > 500))
            return "Matching fields are limited to 500 characters.";

        return DriverMatching.GetValidationError(rule.ManufacturerRegex) ?? DriverMatching.GetValidationError(rule.ModelRegex);
    }

    public Task<ServiceResult<List<Policy>>> GetAvailablePoliciesAsync() => ReadWithPermission(Constants.Permissions.PolicyView, db => db.Policies.AsNoTracking()
        .Where(x => !x.Archived && x.ArchivedAtUtc == null && db.ActiveCompiledPolicies.Any(c => c.PolicyId == x.Id)).OrderBy(x => x.Name).ToListAsync());

    public Task<ServiceResult<List<DeploymentPolicy>>> GetPoliciesAsync(int id) => ReadWithPermission(Constants.Permissions.DeploymentProfileView, db => db.DeploymentPolicies.AsNoTracking()
        .Where(x => x.DeploymentProfileId == id).Include(x => x.Policy).OrderBy(x => x.Position).ToListAsync());

    public Task<ServiceResult> SavePoliciesAsync(int id, IReadOnlyList<int> ids) => Write(nameof(DeploymentProfile), id, "Post-deployment Policies", async db =>
    {
        if (!await db.ImageProfiles.AnyAsync(x => x.Id == id))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.DeploymentProfileNotFound));
        if (ids.Count > 100 || ids.Distinct().Count() != ids.Count)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.SelectUpTo100DifferentPolicies));
        if (await db.Policies.CountAsync(x => ids.Contains(x.Id) && !x.Archived && x.ArchivedAtUtc == null && db.ActiveCompiledPolicies.Any(c => c.PolicyId == x.Id)) != ids.Count)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.EverySelectedPolicyMustBePublishedAndNotArchived));
        await db.DeploymentPolicies.Where(x => x.DeploymentProfileId == id).ExecuteDeleteAsync();
        for (var position = 0; position < ids.Count; position++) db.DeploymentPolicies.Add(new DeploymentPolicy { DeploymentProfileId = id, PolicyId = ids[position], Position = position });
        return ServiceResult.Ok();
    });

    public async Task<ServiceResult<Device>> GetDeviceAsync(int id)
    {
        var result = await ReadDeviceAsync(db => db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id));
        if (!result.Success)
            return ServiceResult<Device>.Fail(result.Error.Code, result.Error.Args);
        return result.Data is null
            ? ServiceResult<Device>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.DeviceNotFound))
            : ServiceResult<Device>.Ok(result.Data);
    }

    public Task<ServiceResult<EffectiveImagingSettings>> GetEffectiveSettingsAsync(int id) => ReadDeviceAsync(async db =>
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return device is null ? new EffectiveImagingSettings(null, null, "Unavailable", "Device not found.")
            : await ImagingInheritance.ResolveAsync(db, device);
    });

    public Task<ServiceResult> SaveGroupDefaultsAsync(int id, int? imageId, int? profileId) => Write(nameof(DeviceGroup), id, "Imaging Settings", async db =>
    {
        var group = await db.DeviceGroups.SingleOrDefaultAsync(x => x.Id == id);
        if (group is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.GroupNotFound));
        if (imageId.HasValue != profileId.HasValue)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Device.DeploymentPairRequired));
        if (imageId.HasValue && (!await db.Images.AnyAsync(x => x.Id == imageId && x.Enabled) || !await db.ImageProfiles.AnyAsync(x => x.Id == profileId && x.Enabled)))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.ChooseAnEnabledImageAndDeploymentProfile));
        group.ImageId = imageId;
        group.DeploymentProfileId = profileId;
        return ServiceResult.Ok();
    });

    public Task<ServiceResult<List<Device>>> GetDevicesAsync(int? imageId = null, int? profileId = null) => ReadWithPermission(Constants.Permissions.DeviceView, db => db.Devices.AsNoTracking()
        .Where(x => (!imageId.HasValue || x.ImageId == imageId) && (!profileId.HasValue || x.DeploymentProfileId == profileId)).OrderBy(x => x.Name).ToListAsync());

    public Task<ServiceResult> SaveDeviceDefaultsAsync(int id, int? imageId, int? profileId) => Write(nameof(Device), id, "Deployment Settings", async db =>
    {
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id);
        if (device is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.DeviceNotFound));
        if (imageId.HasValue != profileId.HasValue)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Device.DeploymentPairRequired));
        if (imageId.HasValue && (!await db.Images.AnyAsync(x => x.Id == imageId && x.Enabled) || !await db.ImageProfiles.AnyAsync(x => x.Id == profileId && x.Enabled)))
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.DeploymentProfileService.ChooseAnEnabledImageAndDeploymentProfile));
        device.ImageId = imageId;
        device.DeploymentProfileId = profileId;
        return ServiceResult.Ok();
    });

    private async Task<ServiceResult<T>> ReadDeviceAsync<T>(Func<CloneDeployDbContext, Task<T>> action)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded &&
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceEdit)).Succeeded &&
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployImage)).Succeeded)
            return ServiceResult<T>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<T>.Ok(await action(db));
    }

    private async Task<ServiceResult<T>> ReadWithPermission<T>(string permission, Func<CloneDeployDbContext, Task<T>> action)
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, permission) is null)
            return ServiceResult<T>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<T>.Ok(await action(db));
    }

    private async Task<ServiceResult> Write(string type, int id, string section, Func<CloneDeployDbContext, Task<ServiceResult>> action, Func<int>? savedId = null, AuditType auditType = AuditType.Update)
    {
        var permission = type switch
        {
            nameof(DeviceGroup) => Constants.Permissions.DeviceGroupEdit,
            nameof(Device) => Constants.Permissions.DeviceEdit,
            nameof(DeploymentProfile) => Constants.Permissions.DeploymentProfileEdit,
            _ => Constants.Permissions.AdminOnly
        };
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, permission);
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var result = await action(db);
        if (!result.Success)
            return result;
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity?.Name ?? "Administrator", AuditType = auditType,
            ObjectType = type, ObjectId = savedId?.Invoke() ?? id, ObjectName = section, ObjectJson = JsonSerializer.Serialize(new { Section = section })
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }
}
