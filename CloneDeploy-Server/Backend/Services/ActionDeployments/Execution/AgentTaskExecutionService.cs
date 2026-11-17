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
/// Provides devices with assigned tasks and records their progress and results.
/// </summary>
public sealed class AgentTaskExecutionService(ICloneDeployDbFactory factory, TimeProvider clock, StoredFileStorage storage, ISecretProtector secrets, DeviceTaskQueue queue, ILogger<AgentTaskExecutionService> logger)
{
    public SharedContentDelivery? Shared { get; init; }
    public IHttpContextAccessor? SiteRequests { get; init; }
    public Task<ServiceResult<AgentTaskOffer?>> ClaimAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, AgentTaskClaim request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => ClaimCoreAsync(deviceId, deviceGuid, leaseToken, request, ct), logger);

    private async Task<ServiceResult<AgentTaskOffer?>> ClaimCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, AgentTaskClaim request, CancellationToken ct)
    {
        var offer = await queue.ClaimAsync(deviceGuid, request.RegularCheckin, ct);
        if (offer is not null) return ServiceResult<AgentTaskOffer?>.Ok(new AgentTaskOffer(offer.TaskId, offer.LeaseToken,
            offer.LeaseExpiresAtUtc, offer.ActionJson, offer.ContentHash));
        await using var db = await factory.CreateContextAsync();
        // An uncertain task requires a result or operator resolution, not repeated claim attempts.
        if (!await db.DeviceTasks.AnyAsync(x => x.DeviceGuid == deviceGuid &&
                (x.Status == DeviceTaskStatus.Running || x.Status == DeviceTaskStatus.Unknown || x.Status == DeviceTaskStatus.Offered), ct)
            && await db.DeviceTasks.AnyAsync(x => x.DeviceGuid == deviceGuid && x.Status == DeviceTaskStatus.Queued
                && x.Deployment.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime
                && (request.RegularCheckin || x.Deployment.Timing == DeviceTaskTiming.Now), ct))
        {
            return ServiceResult<AgentTaskOffer?>.Fail(ServiceErrorCodes.Busy);
        }
        return ServiceResult<AgentTaskOffer?>.Ok(null);
    }

    public Task<ServiceResult> StartAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskLease request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => StartCoreAsync(deviceId, deviceGuid, leaseToken, taskId, request, ct), logger);

    private async Task<ServiceResult> StartCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskLease request, CancellationToken ct)
    {
        return await queue.StartAsync(deviceGuid, taskId, request.LeaseToken, ct) ? ServiceResult.Ok() : ServiceResult.Fail(ServiceErrorCodes.Conflict);
    }

    public Task<ServiceResult> HeartbeatAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskLease request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => HeartbeatCoreAsync(deviceId, deviceGuid, leaseToken, taskId, request, ct), logger);

    private async Task<ServiceResult> HeartbeatCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskLease request, CancellationToken ct)
    {
        return await queue.HeartbeatAsync(deviceGuid, taskId, request.LeaseToken, ct) ? ServiceResult.Ok() : ServiceResult.Fail(ServiceErrorCodes.Conflict);
    }

    public Task<ServiceResult> ResultAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskResult request, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => ResultCoreAsync(deviceId, deviceGuid, leaseToken, taskId, request, ct), logger);

    private async Task<ServiceResult> ResultCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid taskId, AgentTaskResult request, CancellationToken ct)
    {
        var accepted = request.Interrupted
            ? await queue.InterruptAsync(deviceGuid, taskId, request.LeaseToken, request.Detail, ct)
            : await queue.CompleteAsync(deviceGuid, taskId, request.LeaseToken, request.Success, request.Detail, ct);
        return accepted ? ServiceResult.Ok() : ServiceResult.Fail(ServiceErrorCodes.Conflict);
    }

    public Task<ServiceResult<List<WindowsUpdateIdentity>>> WindowsUpdatesAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, Guid actionGuid, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct);
            var action = snapshot is null ? null : AllActions(snapshot).FirstOrDefault(x => x.Guid == actionGuid && WindowsUpdateApprovalPlan.RequiresApproval(x));
            if (action is null) return ServiceResult<List<WindowsUpdateIdentity>>.Fail(ServiceErrorCodes.NotFound);
            return ServiceResult<List<WindowsUpdateIdentity>>.Ok(await WindowsUpdateApprovalPlan.ReadAsync(db, action, ct));
        }, logger);

    public Task<ServiceResult<List<CompiledPolicyActionDto>>> ManagedUpdatesAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct);
            if (snapshot is null || !AllActions(snapshot).Any(ManagedWinGetUpdates.IsUpdater))
                return ServiceResult<List<CompiledPolicyActionDto>>.Fail(ServiceErrorCodes.NotFound);
            return ServiceResult<List<CompiledPolicyActionDto>>.Ok(await ManagedWinGetUpdates.ReadAsync(db, deviceId, runId, true, ct));
        }, logger);

    public Task<ServiceResult<AgentPolicyCredential>> CredentialAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, int credentialId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => CredentialCoreAsync(deviceId, deviceGuid, leaseToken, runId, credentialId, ct), logger);

    public Task<ServiceResult<AgentDomainJoinPlan>> DomainJoinAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, Guid actionGuid, AgentDomainJoinRequest request, CancellationToken ct) =>
        ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            var snapshot = await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct);
            var action = snapshot is null ? null : AllActions(snapshot).FirstOrDefault(x => x.Guid == actionGuid);
            return await DomainJoinPlan.CreateAsync(factory, db, action, request, secrets, ct);
        }, logger);

    private async Task<ServiceResult<AgentPolicyCredential>> CredentialCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, int credentialId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var snapshot = await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct);
        if (snapshot is null || (!AllActions(snapshot).Any(x => x.Settings.TryGetProperty("ManagedCredentialId", out var id)
            && id.ValueKind == JsonValueKind.Number && id.GetInt32() == credentialId) && !await ManagedWinGetUpdates.AllowsCredentialAsync(db, deviceId, runId, true, credentialId, ct))) return ServiceResult<AgentPolicyCredential>.Fail(ServiceErrorCodes.NotFound);
        var credential = await db.ManagedCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credentialId, ct);
        return credential is null ? ServiceResult<AgentPolicyCredential>.Fail(ServiceErrorCodes.NotFound) : ServiceResult<AgentPolicyCredential>.Ok(new AgentPolicyCredential(credential.Username, secrets.Unprotect(credential.PasswordEncrypted)));
    }

    public Task<ServiceResult<Guid>> AcquireDownloadSlotAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => AcquireDownloadSlotCoreAsync(deviceId, deviceGuid, leaseToken, runId, ct), logger);

    private async Task<ServiceResult<Guid>> AcquireDownloadSlotCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct) is null) return ServiceResult<Guid>.Fail(ServiceErrorCodes.NotFound);
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

    public Task<ServiceResult> ReleaseDownloadSlotAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid leaseId, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => ReleaseDownloadSlotCoreAsync(deviceId, deviceGuid, leaseToken, leaseId, ct), logger);

    private async Task<ServiceResult> ReleaseDownloadSlotCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid leaseId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<AgentDownloadLease>().Where(x => x.Id == leaseId && x.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        return ServiceResult.Ok();
    }

    public Task<ServiceResult> DownloadAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, int fileId, Func<Stream, CancellationToken, Task> write, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadCoreAsync(deviceId, deviceGuid, leaseToken, runId, fileId, write, ct), logger);

    private async Task<ServiceResult> DownloadCoreAsync(int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, int fileId, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var snapshot = await ReadRunAsync(db, deviceId, deviceGuid, leaseToken, runId, ct);
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
        }
    }

    private async Task<CompiledPolicyActionDto?> ReadRunAsync(CloneDeployDbContext db, int deviceId, Guid deviceGuid, Guid? leaseToken, Guid runId, CancellationToken ct)
    {
        if (leaseToken is not { } token) return null;
        var json = await db.DeviceTasks.AsNoTracking()
            .Where(x => x.Id == runId && x.DeviceId == deviceId && x.DeviceGuid == deviceGuid
                && x.LeaseToken == token && x.Status == DeviceTaskStatus.Running
                && x.LeaseExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
            .Select(x => x.Deployment.ActionJson).SingleOrDefaultAsync(ct);
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Action").Deserialize<CompiledPolicyActionDto>();
    }

    private static IEnumerable<CompiledPolicyActionDto> AllActions(CompiledPolicyActionDto action)
    {
        yield return action;
        if (action.Condition is { } condition) yield return condition;
    }
}
