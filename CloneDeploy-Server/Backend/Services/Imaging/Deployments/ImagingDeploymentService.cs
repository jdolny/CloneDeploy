using CloneDeploy_Agent.Contracts;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// The imaging workflow calls Prepare before transfer and CompleteImaging only after successful imaging.
/// <summary>
/// Prepares imaging deployments and tracks their completion or cancellation.
/// </summary>
public sealed partial class ImagingDeploymentService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    private async Task<ClaimsPrincipal?> GetGroupDeploymentUserAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ? user : null;
    }

    public async Task<ServiceResult<ImagingDeployment>> PrepareGroupMemberAsync(int groupId, int deviceId, int imageId, int profileId)
    {
        var user = await GetGroupDeploymentUserAsync(Constants.Permissions.DeviceGroupDeployImage);
        if (user is null) return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await db.DeviceGroups.AnyAsync(g => g.Id == groupId && g.ImageId == imageId && g.DeploymentProfileId == profileId) ||
            !await DeviceGroupMembershipQuery.DeviceIds(db, [groupId]).ContainsAsync(deviceId))
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.TheGroupSettingsOrMembershipChangedReloadTheReview));
        return await PrepareCoreAsync(deviceId, imageId, profileId, user);
    }

    public async Task<ServiceResult<ImagingDeployment>> PrepareAsync(int deviceId, int? imageId = null, int? profileId = null)
    {
        var user = await GetDeviceImagingUserAsync();
        if (user is null)
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        return await PrepareCoreAsync(deviceId, imageId, profileId, user);
    }

    // Browser device workflow: explicit version testing needs both catalog access and
    // the independent unapproved-deployment grant. Imager selection is authorized separately.
    public async Task<ServiceResult<ImagingDeployment>> PrepareVersionAsync(int deviceId, int imageId, int profileId, Guid versionId)
    {
        var user = await GetDeviceImagingUserAsync();
        if (user is null || user.FindFirstValue("imaging_kind") is not null ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.ImageView)).Succeeded ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.ImageDeployUnapproved)).Succeeded)
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        return await PrepareCoreAsync(deviceId, imageId, profileId, user, null, false, null, null, versionId);
    }

    public async Task<ServiceResult<ImagingDeployment>> PrepareAssignedAsync(int deviceId, int imageId, int profileId)
    {
        var user = await GetDeviceImagingUserAsync();
        if (user is null) return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        return await PrepareCoreAsync(deviceId, imageId, profileId, user, null, requireAssigned: true);
    }

    public async Task<ServiceResult<ImagingDeployment>> PrepareAgentBootAsync(AgentImagingReview review)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployImageThroughAgent)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        return await PrepareCoreAsync(review.DeviceId, null, null, user, review);
    }
    private Task<ServiceResult<ImagingDeployment>> PrepareCoreAsync(int deviceId, int? imageId, int? profileId, ClaimsPrincipal user)
        => PrepareCoreAsync(deviceId, imageId, profileId, user, null);

    private async Task<ServiceResult<ImagingDeployment>> PrepareCoreAsync(int deviceId, int? imageId, int? profileId, ClaimsPrincipal user, AgentImagingReview? boot, bool requireAssigned = false)
        => await PrepareCoreAsync(deviceId, imageId, profileId, user, boot, requireAssigned, null);

    private async Task<ServiceResult<ImagingDeployment>> PrepareCoreAsync(int deviceId, int? imageId, int? profileId, ClaimsPrincipal user, AgentImagingReview? boot, bool requireAssigned, int? manualDriverPackId)
        => await PrepareCoreAsync(deviceId, imageId, profileId, user, boot, requireAssigned, manualDriverPackId, null, null);

    private async Task<ServiceResult<ImagingDeployment>> PrepareCoreAsync(int deviceId, int? imageId, int? profileId, ClaimsPrincipal user, AgentImagingReview? boot, bool requireAssigned, int? manualDriverPackId, Guid? multicastId, Guid? selectedVersionId)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})");
        MulticastSession? multicast = null;
        if (multicastId is { } sessionId)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)");
            multicast = await db.Set<MulticastSession>().SingleOrDefaultAsync(x => x.Id == sessionId);
            if (multicast is null || multicast.State > MulticastState.Waiting || multicast.StartRequested)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.TheMulticastSessionIsNoLongerAcceptingDevices));
            profileId = multicast.DeploymentProfileId;
        }
        var device = await db.Devices.Include(x => x.ComputerSystem).Include(x => x.Os).SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
        if (device is null)
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.DeviceNotFoundOrArchived));
        if (imageId.HasValue != profileId.HasValue)
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.SelectBothAnImageAndADeploymentProfile));
        if (requireAssigned || imageId is null)
        {
            var effective = await ImagingInheritance.ResolveAsync(db, device);
            if (effective.Error is not null)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", effective.Error);
            if (requireAssigned && (effective.ImageId != imageId || effective.ProfileId != profileId))
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.EffectiveAssignmentsChangedReloadTheReviewBeforeQueuingDeployment));
            imageId = effective.ImageId;
            profileId = effective.ProfileId;
        }
        if (imageId is null || profileId is null)
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.AnImageAndDeploymentProfileAreRequired));
        if (await db.Set<ImagingDeployment>().AnyAsync(x => x.DeviceId == deviceId && x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null))
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.ThisDeviceAlreadyHasAnUnfinishedDeploymentFinishOr));
        if (await db.ActiveImagingTasks.AnyAsync(x => x.DeviceId == deviceId && x.UploadId != null && x.Status < ImagingStatus.Succeeded))
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.ThisDeviceHasAnActiveCaptureFinishOrCancel));
        if (!await db.Images.AnyAsync(x => x.Id == imageId && x.Enabled))
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.ImageIsMissingOrDisabled));
        var fromImager = user.FindFirstValue("imaging_kind") == "user";
        var profile = await db.ImageProfiles.SingleOrDefaultAsync(x => x.Id == profileId && x.Enabled && (!fromImager || requireAssigned || x.VisibleInWinPe));
        if (profile is null)
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.DeploymentProfileIsMissingOrDisabled));
        var multicastVersion = multicast?.ImageVersionId ?? selectedVersionId;
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == imageId && (multicastVersion == null ? x.IsActive : x.Id == multicastVersion));
        if (version is null || (multicast is null ? !await ImageReplicaCatalog.Providers(db, version).AnyAsync() :
                !await MulticastQueue.Providers(db, version).AnyAsync(x => x.Id == multicast.StorageProviderId)))
            return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.TheImageNeedsAnActiveVersionOnAnEligible));
        if (!version.IsApproved && !(fromImager
                ? int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var operatorId) &&
                    await ImagingPermissions.HasAsync(db, operatorId, Constants.Permissions.ImageDeployUnapproved)
                : (await authorization.AuthorizeAsync(user, Constants.Permissions.ImageDeployUnapproved)).Succeeded))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        if (boot is not null)
        {
            if (device.Os?.NativeArchitecture != boot.Architecture)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.DeviceArchitectureChangedRefreshInventoryAndReloadTheReview));
            var variant = AgentWinPeSelection.Resolve(await db.WinPeDefinitions.Include(x => x.PublishedBuild).ToListAsync(), boot.Architecture, device.ComputerSystem?.Manufacturer, device.ComputerSystem?.Model);
            if (device.Guid != boot.DeviceGuid || imageId != boot.ImageId || profileId != boot.ProfileId || version.Id != boot.ImageVersionId || variant.PublishedBuildId != boot.BuildId)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.AssignmentsChangedReloadTheReview));
        }
        var pack = profile.DriverMode == DeploymentDriverMode.Specific ? profile.DriverPackId
            : profile.DriverMode == DeploymentDriverMode.Automatic ? manualDriverPackId : null;
        if (pack == 0) pack = null;
        var deployment = new ImagingDeployment
        {
            AgentBootPlanJson = boot is null ? null : JsonSerializer.Serialize(boot),
            Id = Guid.NewGuid(), DeviceId = deviceId, ImageVersionId = version.Id,
            MulticastSessionId = multicastId,
            DeploymentProfileId = profile.Id, CreatedAtUtc = DateTime.UtcNow,
            SettingsJson = JsonSerializer.Serialize(new DeploymentSettingsDto(imageId.Value, version.Id, version.StorageProviderId, version.RelativePath,
                profile.Id, profile.Name, pack, device.Name)
                { AutomaticDrivers = profile.DriverMode == DeploymentDriverMode.Automatic && manualDriverPackId is null,
                    SetComputerName = profile.SetComputerName, AfterDeployment = profile.AfterDeployment, OuNamingRuleSetId = profile.OuNamingRuleSetId, OuPrompt = profile.OuPrompt })
        };
        var policies = await db.DeploymentPolicies.Where(x => x.DeploymentProfileId == profile.Id).OrderBy(x => x.Position).ToListAsync();
        foreach (var link in policies)
        {
            var json = await db.ActiveCompiledPolicies.Where(x => x.PolicyId == link.PolicyId && !x.Policy!.Archived && x.Policy.ArchivedAtUtc == null).Select(x => x.Json).SingleOrDefaultAsync();
            if (json is null)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.APostDeploymentPolicyIsNoLongerPublishedUpdate));
            var frozen = FreezePolicy(json, deployment.Id);
            if (frozen.Error is not null)
                return ServiceResult<ImagingDeployment>.Fail("Image.Validation", frozen.Error);
            var snapshot = frozen.Snapshot!;
            deployment.Policies.Add(new ImagingDeploymentPolicy
            {
                Id = Guid.NewGuid(), PolicyId = link.PolicyId, Position = link.Position,
                Hash = snapshot.Hash, SnapshotJson = JsonSerializer.Serialize(snapshot)
            });
        }

        if (fromImager || boot is not null)
        {
            var unavailable = ManualImageUnavailable(version);
            if (unavailable is not null) return ServiceResult<ImagingDeployment>.Fail("Image.Validation", unavailable);
        }
        db.Add(deployment);
        var activeTask = await DeploymentTaskTracking.SyncAsync(db, deployment);
        if (boot is not null) activeTask.StatusMessage = "Waiting for agent to prepare WinPE";
        activeTask.AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Audit(db, user, deviceId, deployment.Id, "Prepared");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<ImagingDeployment>.Ok(deployment);
    }

    public Task<ServiceResult> CompleteImagingAsync(Guid id, int deviceId) => ChangeState(id, deviceId, false);
    public Task<ServiceResult> CancelAsync(Guid id, int deviceId) => ChangeState(id, deviceId, true);

    public async Task<ServiceResult> RecoverPolicyAsync(Guid id, int deviceId, Guid stepId, string expectedHash, bool skip)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization);
        var user = access?.User;
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployImage)).Succeeded &&
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployImageThroughAgent)).Succeeded &&
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceGroupDeployImage)).Succeeded &&
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceGroupDeployMulticast)).Succeeded)
            return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})");
        var owner = await db.ActiveImagingTasks.Where(x => x.DeploymentId == id && x.DeviceId == deviceId).Select(x => x.AppUserId).SingleOrDefaultAsync();
        if (!access!.CanManage(owner)) return ServiceResult.Fail("Image.Forbidden");
        var deployment = await db.Set<ImagingDeployment>().Include(x => x.Policies)
            .SingleOrDefaultAsync(x => x.Id == id && x.DeviceId == deviceId);
        if (deployment is null || deployment.CancelledAtUtc != null || deployment.SetupCompletedAtUtc != null
            || deployment.ImagingCompletedAtUtc == null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.ThisDeploymentIsNotAwaitingSetupRecovery));
        var step = deployment.Policies.OrderBy(x => x.Position)
            .FirstOrDefault(x => x.FinishedAtUtc == null || x.Result == (int)PolicyRunResult.Failed);
        if (step is null || step.Id != stepId || step.Hash != expectedHash
            || step.FinishedAtUtc == null || step.Result != (int)PolicyRunResult.Failed)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.TheFailedStepHasChangedRefreshBeforeRecoveringIt));

        var previousRun = step.AgentPolicyRunId;
        if (skip)
        {
            step.Result = (int)PolicyRunResult.Skipped;
            step.FinishedAtUtc = DateTime.UtcNow;
            if (deployment.Policies.All(x => x.FinishedAtUtc != null && x.Result != (int)PolicyRunResult.Failed))
                deployment.SetupCompletedAtUtc = DateTime.UtcNow;
        }
        else
        {
            var snapshot = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(step.SnapshotJson)!;
            var settings = JsonNode.Parse(snapshot.Policy.Settings.GetRawText())!.AsObject();
            settings["ImagingRetryId"] = Guid.NewGuid().ToString();
            var content = snapshot.Policy with { Settings = JsonSerializer.SerializeToElement(settings) };
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
            step.SnapshotJson = JsonSerializer.Serialize(snapshot with { Policy = content, Hash = hash, ReRunExisting = true });
            step.Hash = hash;
            step.AgentPolicyRunId = null;
            step.FinishedAtUtc = null;
            step.Result = null;
        }
        var operation = $"{(skip ? "Skipped" : "Retry requested")}: policy step {step.Id}, previous run {previousRun}";
        Audit(db, user, deviceId, id, operation);
        db.DeviceImagingLogs.Add(new DeviceImagingLog { DeviceId = deviceId, Type = "Setup", SubType = skip ? "Skipped" : "Retry",
            Contents = $"Deployment {id}: {operation}. Administrator: {user.Identity?.Name}." });
        await db.SaveChangesAsync();
        await DeploymentTaskTracking.SyncAsync(db, deployment);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult> ChangeState(Guid id, int deviceId, bool cancel)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization);
        var user = cancel ? access?.User : await GetAdminAsync();
        if (user is null)
            return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})");
        if (cancel)
        {
            var owner = await db.ActiveImagingTasks.Where(x => x.DeploymentId == id && x.DeviceId == deviceId).Select(x => x.AppUserId).SingleOrDefaultAsync();
            if (!access!.CanManage(owner)) return ServiceResult.Fail("Image.Forbidden");
        }
        var deployment = await db.Set<ImagingDeployment>().Include(x => x.Policies).SingleOrDefaultAsync(x => x.Id == id && x.DeviceId == deviceId);
        if (deployment is null)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.DeploymentNotFoundForThisDevice));
        if (cancel)
        {
            if (deployment.Policies.Any(x => x.AgentPolicyRunId != null && x.FinishedAtUtc == null))
                return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.APostDeploymentPolicyIsStillRunningWaitFor));
            deployment.CancelledAtUtc ??= DateTime.UtcNow;
        }
        else
        {
            if (deployment.CancelledAtUtc != null)
                return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingDeploymentService.ThisDeploymentWasCancelled));
            deployment.ImagingCompletedAtUtc ??= DateTime.UtcNow;
            if (deployment.Policies.Count == 0) deployment.SetupCompletedAtUtc ??= DateTime.UtcNow;
        }

        if (await db.ActiveImagingTasks.AnyAsync(x => x.DeploymentId == id)) await DeploymentTaskTracking.SyncAsync(db, deployment);
        Audit(db, user, deviceId, id, cancel ? "Cancelled" : "Imaging completed");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static (CompiledPolicySnapshotDto? Snapshot, string? Error) FreezePolicy(string json, Guid deploymentId)
    {
        var original = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(json);
        if (original is null)
            return (null, "Invalid published policy.");
        if (original.SchemaVersion is not (1 or 3))
            return (null, "Unsupported published policy format.");
        var settings = JsonNode.Parse(original.Policy.Settings.GetRawText())!.AsObject();
        if (settings["Trigger"]?.GetValue<int>() == (int)PolicyTrigger.SelfServiceOnly)
            return (null, "Self-service-only policies cannot run automatically after imaging.");
        settings["ImagingDeploymentId"] = deploymentId.ToString();
        settings["Trigger"] = 0;
        settings["Frequency"] = 1;
        settings["StartDateUtc"] = DateTime.UnixEpoch;
        settings["SkipServerResult"] = false;
        settings.Remove("AllowedStartWindow");
        var content = original.Policy with { Settings = JsonSerializer.SerializeToElement(settings), StartWindow = null, EndWindow = null };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
        return (new CompiledPolicySnapshotDto(1, hash, true, content), null);
    }

    private async Task<ClaimsPrincipal?> GetDeviceImagingUserAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceDeployImage)).Succeeded &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ? user : null;
    }

    private async Task<ClaimsPrincipal?> GetAdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _))
            return null;
        return user;
    }

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, int deviceId, Guid id, string operation) => db.AuditLogs.Add(new AuditLog
    {
        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update,
        ObjectType = nameof(Device), ObjectId = deviceId, ObjectName = "Imaging deployment", ObjectJson = JsonSerializer.Serialize(new { DeploymentId = id, Operation = operation })
    });

}
