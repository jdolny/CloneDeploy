using System.Data;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Supplies policies and their files to devices and records execution progress and results.
/// </summary>
public sealed class AgentPolicyExecutionService(ICloneDeployDbFactory factory, TimeProvider clock, StoredFileStorage storage, ISecretProtector secrets, ILogger<AgentPolicyExecutionService> logger,
    StorageReadSelection? readSelection = null, IHttpContextAccessor? requests = null)
{
    public SharedContentDelivery? Shared { get; init; }
    public IHttpContextAccessor? SiteRequests { get; init; }
    public Task<ServiceResult<AgentSetupReadinessReceipt>> ReportSetupReadinessAsync(int deviceId, AgentSetupReadiness request, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            if (request.Reason?.Length > 512) return ServiceResult<AgentSetupReadinessReceipt>.Fail(ServiceErrorCodes.Invalid);
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})", ct);
            var deployment = await db.Set<ImagingDeployment>().SingleOrDefaultAsync(x => x.DeviceId == deviceId
                && x.ImagingCompletedAtUtc != null && x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null, ct);
            if (deployment is not null)
            {
                deployment.WindowsSetupReadyAtUtc = request.Ready ? clock.GetUtcNow().UtcDateTime : null;
                deployment.SetupReadinessMessage = request.Ready ? null : request.Reason;
                await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return ServiceResult<AgentSetupReadinessReceipt>.Ok(new(deployment is not null));
        }, logger);

    public Task<ServiceResult<List<CompiledPolicySnapshotDto>>> GetForUserAsync(int deviceId, string sid, bool selfService, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(sid) || sid.Length > 184) return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail(ServiceErrorCodes.Invalid);
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            return ServiceResult<List<CompiledPolicySnapshotDto>>.Ok(await PolicyAudienceDelivery.ReadAsync(db, deviceId, sid, selfService, ct));
        }, logger);
    public Task<ServiceResult<List<CompiledPolicySnapshotDto>>> GetAsync(int deviceId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => GetCoreAsync(deviceId, ct), logger);

    private async Task<ServiceResult<List<CompiledPolicySnapshotDto>>> GetCoreAsync(int deviceId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var guid = await db.Devices.Where(x => x.Id == deviceId && x.ArchivedAtUtc == null && x.ProvisionStatus == DeviceProvisionStatus.Provisioned)
            .Select(x => (Guid?)x.Guid).SingleOrDefaultAsync(ct);
        if (guid is null) return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail(ServiceErrorCodes.Forbidden);
        var admission = new DeviceTaskQueue(factory, clock);
        var lease = await admission.TryBeginCheckinAsync(guid.Value, ct);
        if (lease is null)
        {
            return ServiceResult<List<CompiledPolicySnapshotDto>>.Fail(ServiceErrorCodes.Busy);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(110));
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token);
            return ServiceResult<List<CompiledPolicySnapshotDto>>.Ok(await PolicyAudienceDelivery.ReadAsync(db, deviceId, null, false, deadline.Token));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await admission.EndCheckinAsync(guid.Value, lease.Id, cleanup.Token);
        }
    }

    public Task<ServiceResult<AgentPolicyReceipt>> StartAsync(int deviceId, AgentPolicyStart request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => StartCoreAsync(deviceId, request, ct), logger);

    private async Task<ServiceResult<AgentPolicyReceipt>> StartCoreAsync(int deviceId, AgentPolicyStart request, CancellationToken ct)
    {
        if (request.RunId == Guid.Empty || request.Hash.Length != 64 || request.UserSid?.Length > 184) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Invalid);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize admission for this device, including duplicate deliveries after a lost response.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})", ct);
        // Admission and audience edits have a defined order; no partly saved audience can be admitted.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947234, {request.PolicyId})", ct);
        var features = await AgentFeatureSettings.FeaturesAsync(db, ct);
        if (request.SelfService && !features.SelfServiceEnabled) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, UiText.Get(LocKeys.Ui.AgentPolicyExecutionService.SelfServiceIsDisabledByYourAdministrator));
        var existing = await db.Set<AgentPolicyRun>().FindAsync([request.RunId], ct);
        if (existing is not null)
        {
            var recipients = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(existing.SnapshotJson)?.NotificationRecipientSids;
            if (recipients is not null && !request.SupportsNotificationRecipients)
                return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, UiText.Get(LocKeys.Ui.AgentPolicyExecutionService.UpdateTheAgentToUseNotificationRecipientAudiences));
            return existing.DeviceId == deviceId && existing.PolicyId == request.PolicyId && existing.Hash == request.Hash &&
                existing.UserName == request.UserName && existing.SelfService == request.SelfService &&
                (existing.UserSid == request.UserSid || existing.UserSid is null && !existing.SelfService)
                ? ServiceResult<AgentPolicyReceipt>.Ok(new AgentPolicyReceipt(existing.Id, existing.FinishedAtUtc != null) { NotificationRecipientSids = recipients }) : ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Conflict);
        }
        if (request.SelfService && string.IsNullOrWhiteSpace(request.UserSid)) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden);
        var snapshot = (await PolicyAudienceDelivery.ReadAsync(db, deviceId, request.UserSid, request.SelfService, ct))
            .SingleOrDefault(x => x.Policy.Id == request.PolicyId && x.Hash == request.Hash);
        if (snapshot is null) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.NotFound);
        if (!features.WinGetEnabled && AgentFeatureSettings.UsesWinGet(snapshot.Policy))
            return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, AgentFeatureSettings.WinGetDisabled);
        var recipientAudience = await db.Set<PolicyUserAccess>().Where(x => x.PolicyId == request.PolicyId).Select(x => x.LoginAudienceId).SingleOrDefaultAsync(ct);
        if (recipientAudience is int audienceId && snapshot.Policy.Settings.GetProperty("Trigger").GetInt32() != (int)PolicyTrigger.Login)
        {
            if (!request.SupportsNotificationRecipients)
                return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, UiText.Get(LocKeys.Ui.AgentPolicyExecutionService.UpdateTheAgentToUseNotificationRecipientAudiences));
            if (!await NotificationPolicyAudience.EligibleAsync(db, request.PolicyId))
                return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, NotificationPolicyAudience.ValidationMessage);
            await UserAudienceService.LockAsync(db);
            snapshot = snapshot with { NotificationRecipientSids = await NotificationPolicyAudience.RecipientsAsync(db, audienceId, ct) };
        }
        if (snapshot.Policy.Settings.TryGetProperty("ImagingDeploymentId", out var deploymentValue))
        {
            if (!Guid.TryParse(deploymentValue.GetString(), out var deploymentId)) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Conflict);
            var step = await db.Set<ImagingDeploymentPolicy>().SingleOrDefaultAsync(x => x.ImagingDeploymentId == deploymentId &&
                x.Deployment!.DeviceId == deviceId && x.PolicyId == request.PolicyId && x.Hash == request.Hash, ct);
            if (step is null || step.AgentPolicyRunId != null || step.FinishedAtUtc != null) return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Conflict);
            step.AgentPolicyRunId = request.RunId;
        }
        db.Add(new AgentPolicyRun { Id = request.RunId, DeviceId = deviceId, PolicyId = request.PolicyId,
            Hash = request.Hash, SnapshotJson = JsonSerializer.Serialize(snapshot),
            UserName = request.UserName, UserSid = request.UserSid, SelfService = request.SelfService, StartedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        if (!features.WinGetEnabled && AgentFeatureSettings.UsesWinGet(snapshot.Policy))
            return ServiceResult<AgentPolicyReceipt>.Fail(ServiceErrorCodes.Forbidden, AgentFeatureSettings.WinGetDisabled);
        if (snapshot.Policy.Settings.TryGetProperty("ImagingDeploymentId", out var startedDeployment))
        {
            var deployment = await db.Set<ImagingDeployment>().SingleAsync(x => x.Id == Guid.Parse(startedDeployment.GetString()!), ct);
            await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return ServiceResult<AgentPolicyReceipt>.Ok(new AgentPolicyReceipt(request.RunId, false) { NotificationRecipientSids = snapshot.NotificationRecipientSids });
    }

    public Task<ServiceResult> CompleteAsync(int deviceId, AgentPolicyResult request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => CompleteCoreAsync(deviceId, request, ct), logger);

    private async Task<ServiceResult> CompleteCoreAsync(int deviceId, AgentPolicyResult request, CancellationToken ct)
    {
        if (request.Result is < 0 or > 3) return ServiceResult.Fail(ServiceErrorCodes.Invalid);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})", ct);
        var run = await db.Set<AgentPolicyRun>().SingleOrDefaultAsync(x => x.Id == request.RunId && x.DeviceId == deviceId, ct);
        if (run is null) return ServiceResult.Fail(ServiceErrorCodes.NotFound);
        if (run.FinishedAtUtc != null)
            return run.Result == request.Result && run.Detail == request.Detail ? ServiceResult.Ok() : ServiceResult.Fail(ServiceErrorCodes.Conflict);
        var snapshot = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(run.SnapshotJson)!;
        run.Result = request.Result;
        run.Detail = request.Detail;
        run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        var deploymentStep = await db.Set<ImagingDeploymentPolicy>().Include(x => x.Deployment)
            .SingleOrDefaultAsync(x => x.AgentPolicyRunId == request.RunId && x.Deployment!.DeviceId == deviceId, ct);
        if (deploymentStep is not null)
        {
            deploymentStep.FinishedAtUtc = run.FinishedAtUtc;
            deploymentStep.Result = request.Result;
            if (request.Result != (int)PolicyRunResult.Failed && !await db.Set<ImagingDeploymentPolicy>().AnyAsync(x =>
                    x.ImagingDeploymentId == deploymentStep.ImagingDeploymentId && x.Id != deploymentStep.Id &&
                    (x.FinishedAtUtc == null || x.Result == (int)PolicyRunResult.Failed), ct))
                deploymentStep.Deployment!.SetupCompletedAtUtc = run.FinishedAtUtc;
        }
        if (!snapshot.Policy.Settings.GetProperty("SkipServerResult").GetBoolean())
            db.PolicyHistories.Add(new PolicyHistory { DeviceId = deviceId, PolicyId = run.PolicyId,
                Hash = run.Hash, Result = (PolicyRunResult)request.Result, LastRanAtUtc = run.FinishedAtUtc.Value, UserName = run.UserName });
        await db.SaveChangesAsync(ct);
        if (deploymentStep is not null)
        {
            await DeploymentTaskTracking.SyncAsync(db, deploymentStep.Deployment!, ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    public Task<ServiceResult<List<WindowsUpdateIdentity>>> WindowsUpdatesAsync(int deviceId, Guid runId, Guid actionGuid, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, runId, ct);
            var action = snapshot is null ? null : AllActions(snapshot).FirstOrDefault(x => x.Guid == actionGuid && WindowsUpdateApprovalPlan.RequiresApproval(x));
            if (action is null) return ServiceResult<List<WindowsUpdateIdentity>>.Fail(ServiceErrorCodes.NotFound);
            return ServiceResult<List<WindowsUpdateIdentity>>.Ok(await WindowsUpdateApprovalPlan.ReadAsync(db, action, ct));
        }, logger);

    public Task<ServiceResult<List<CompiledPolicyActionDto>>> ManagedUpdatesAsync(int deviceId, Guid runId, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, runId, ct);
            if (snapshot is null || !AllActions(snapshot).Any(ManagedWinGetUpdates.IsUpdater))
                return ServiceResult<List<CompiledPolicyActionDto>>.Fail(ServiceErrorCodes.NotFound);
            return ServiceResult<List<CompiledPolicyActionDto>>.Ok(await ManagedWinGetUpdates.ReadAsync(db, deviceId, runId, false, ct));
        }, logger);

    public Task<ServiceResult<AgentPolicyCredential>> CredentialAsync(int deviceId, Guid runId, int credentialId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => CredentialCoreAsync(deviceId, runId, credentialId, ct), logger);

    public Task<ServiceResult<AgentDomainJoinPlan>> DomainJoinAsync(int deviceId, Guid runId, Guid actionGuid, AgentDomainJoinRequest request, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, runId, ct);
            var action = snapshot is null ? null : AllActions(snapshot).FirstOrDefault(x => x.Guid == actionGuid);
            return await DomainJoinPlan.CreateAsync(factory, db, action, request, secrets, ct);
        }, logger);

    private async Task<ServiceResult<AgentPolicyCredential>> CredentialCoreAsync(int deviceId, Guid runId, int credentialId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var snapshot = await ReadRunAsync(db, deviceId, runId, ct);
        if (snapshot is null || (!AllActions(snapshot).Any(x => x.Settings.TryGetProperty("ManagedCredentialId", out var id)
            && id.ValueKind == JsonValueKind.Number && id.GetInt32() == credentialId) && !await ManagedWinGetUpdates.AllowsCredentialAsync(db, deviceId, runId, false, credentialId, ct))) return ServiceResult<AgentPolicyCredential>.Fail(ServiceErrorCodes.NotFound);
        var credential = await db.ManagedCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credentialId, ct);
        return credential is null ? ServiceResult<AgentPolicyCredential>.Fail(ServiceErrorCodes.NotFound) : ServiceResult<AgentPolicyCredential>.Ok(new AgentPolicyCredential(credential.Username, secrets.Unprotect(credential.PasswordEncrypted)));
    }

    public Task<ServiceResult<Guid>> AcquireDownloadSlotAsync(int deviceId, Guid runId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => AcquireDownloadSlotCoreAsync(deviceId, runId, ct), logger);

    private async Task<ServiceResult<Guid>> AcquireDownloadSlotCoreAsync(int deviceId, Guid runId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (await ReadRunAsync(db, deviceId, runId, ct) is null) return ServiceResult<Guid>.Fail(ServiceErrorCodes.NotFound);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903418)", ct);
        await db.Set<AgentDownloadLease>().Where(x => x.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime).ExecuteDeleteAsync(ct);
        var limits = await DeviceTaskQueue.ReadLimitsAsync(db);
        if (await db.Set<AgentDownloadLease>().CountAsync(ct) >= limits.MaxConcurrentDownloads)
        {
            return ServiceResult<Guid>.Fail(ServiceErrorCodes.Busy);
        }
        var lease = new AgentDownloadLease { Id = Guid.NewGuid(), DeviceId = deviceId, ExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddHours(2) };
        db.Add(lease);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult<Guid>.Ok(lease.Id);
    }

    public Task<ServiceResult> ReleaseDownloadSlotAsync(int deviceId, Guid leaseId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => ReleaseDownloadSlotCoreAsync(deviceId, leaseId, ct), logger);

    private async Task<ServiceResult> ReleaseDownloadSlotCoreAsync(int deviceId, Guid leaseId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<AgentDownloadLease>().Where(x => x.Id == leaseId && x.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        return ServiceResult.Ok();
    }

    public Task<ServiceResult> DownloadAsync(int deviceId, Guid runId, int fileId, Func<Stream, CancellationToken, Task> write, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadCoreAsync(deviceId, runId, fileId, write, ct), logger);

    private async Task<ServiceResult> DownloadCoreAsync(int deviceId, Guid runId, int fileId, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var snapshot = await ReadRunAsync(db, deviceId, runId, ct);
        var expected = snapshot is null ? null : AllActions(snapshot).SelectMany(x => x.Files).FirstOrDefault(x => x.Id == fileId);
        if (expected is null) return ServiceResult.Fail(ServiceErrorCodes.NotFound);
        var file = await db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId, ct);
        if (file is null || file.Status != StoredFileStatus.Complete || file.DirectoryGuid != expected.DirectoryGuid
            || file.Size != expected.Size || !string.Equals(file.Sha256Hash, expected.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            return ServiceResult.Fail(ServiceErrorCodes.Conflict, UiText.Get(LocKeys.Ui.AgentTaskExecutionService.PublishedPolicyContentChangedPublishThePolicyAgain));
        var lease = new AgentDownloadLease { Id = Guid.NewGuid(), DeviceId = deviceId, ExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddHours(2) };
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903418)", ct);
            await db.Set<AgentDownloadLease>().Where(x => x.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime).ExecuteDeleteAsync(ct);
            var limits = await DeviceTaskQueue.ReadLimitsAsync(db);
            if (await db.Set<AgentDownloadLease>().CountAsync(ct) >= limits.MaxConcurrentDownloads)
            {
                return ServiceResult.Fail(ServiceErrorCodes.Busy);
            }
            db.Add(lease);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromHours(2));
            async Task<Stream> PrimaryAsync()
            {
                await storage.EnsureReadyAsync(file, deadline.Token);
                return new FileStream(storage.NamedPath(file), FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            if (SiteRequests?.HttpContext is { } context) context.Items[SiteDownloadDelivery.DownloadSlotKey] = lease.Id;
            await using var stream = Shared is null ? await PrimaryAsync() : await Shared.OpenManagementAsync(file, PrimaryAsync, deadline.Token);
            if (stream.Length != expected.Size) return ServiceResult.Fail(ServiceErrorCodes.Conflict, UiText.Get(LocKeys.Ui.AgentTaskExecutionService.PublishedPolicyContentChanged));
            if (Shared is null && readSelection is not null && file.StorageProviderId is { } provider)
                await readSelection.ReserveAsync(lease.Id, $"file:{file.Id}:{file.Sha256Hash}", StoragePurpose.DeviceManagement,
                    [provider], requests?.HttpContext?.Connection.RemoteIpAddress, TimeSpan.FromHours(2), deadline.Token);
            await write(stream, deadline.Token);
            return ServiceResult.Ok();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (SiteDownloadDelivery.HasOffer(SiteRequests?.HttpContext))
                await db.Set<AgentDownloadLease>().Where(x => x.Id == lease.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, clock.GetUtcNow().UtcDateTime.AddMinutes(2)), cleanup.Token);
            else
            await db.Set<AgentDownloadLease>().Where(x => x.Id == lease.Id).ExecuteDeleteAsync(cleanup.Token);
            if (readSelection is not null && !SiteDownloadDelivery.HasOffer(SiteRequests?.HttpContext)) await readSelection.ReleaseAsync(lease.Id, cleanup.Token);
        }
    }

    private async Task<CompiledPolicySnapshotDto?> ReadRunAsync(CloneDeployDbContext db, int deviceId, Guid runId, CancellationToken ct)
    {
        var run = await db.Set<AgentPolicyRun>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId && x.DeviceId == deviceId && x.FinishedAtUtc == null, ct);
        return run is null ? null : JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(run.SnapshotJson);
    }

    private static IEnumerable<CompiledPolicyActionDto> AllActions(CompiledPolicySnapshotDto snapshot)
    {
        if (snapshot.Policy.Condition is { } condition) yield return condition;
        foreach (var action in snapshot.Policy.Actions)
        {
            yield return action;
            if (action.Condition is { } nested) yield return nested;
        }
    }
}
