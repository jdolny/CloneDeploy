using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Trusted agent integration boundary, not an unauthenticated API. The caller must derive the
// device GUID from authenticated agent identity. Agents persist TaskId locally before executing.
/// <summary>
/// Manages waiting device tasks and controls when they can start.
/// </summary>
public sealed class DeviceTaskQueue(ICloneDeployDbFactory factory, TimeProvider clock)
{
    internal const string InventoryLimitKey = "Agent.MaxConcurrentInventorySubmissions";
    internal const string DownloadLimitKey = "Agent.MaxConcurrentDownloads";
    internal const string CheckinIntervalKey = "Agent.CheckinIntervalMinutes";
    internal const string TaskLimitKey = "DeviceTasks.MaxConcurrentTasks";
    internal const string CheckinLimitKey = "DeviceTasks.MaxConcurrentCheckins";

    internal static Task LockAsync(CloneDeployDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903417)", ct);

    internal static async Task<DeviceTaskLimitsDto> ReadLimitsAsync(CloneDeployDbContext db)
    {
        var values = await db.AppSettings.Where(x => x.Name == AgentEnrollmentRateSettings.Key || x.Name == InventoryLimitKey || x.Name == TaskLimitKey || x.Name == CheckinLimitKey || x.Name == CheckinIntervalKey || x.Name == DownloadLimitKey).ToDictionaryAsync(x => x.Name, x => x.Value);
        return new DeviceTaskLimitsDto
        {
            EnrollmentRequestsPerMinute = AgentEnrollmentRateSettings.Parse(values.GetValueOrDefault(AgentEnrollmentRateSettings.Key)),
            MaxConcurrentInventorySubmissions = int.TryParse(values.GetValueOrDefault(InventoryLimitKey), out var inventory) && inventory is >= 1 and <= 10000 ? inventory : 10,
            MaxConcurrentDownloads = int.TryParse(values.GetValueOrDefault(DownloadLimitKey), out var downloads) && downloads is >= 1 and <= 10000 ? downloads : 20,
            CheckinIntervalMinutes = int.TryParse(values.GetValueOrDefault(CheckinIntervalKey), out var interval) && interval is >= 1 and <= 1440 ? interval : 60,
            MaxConcurrentTasks = int.TryParse(values.GetValueOrDefault(TaskLimitKey), out var tasks) && tasks > 0 ? tasks : 20,
            MaxConcurrentCheckins = int.TryParse(values.GetValueOrDefault(CheckinLimitKey), out var checkins) && checkins > 0 ? checkins : 100
        };
    }

    // The transport must enforce this deadline on the HTTP request and release the lease in finally.
    // Task execution continues independently after the short check-in admission is released.
    public async Task<DeviceCheckinLease?> TryBeginCheckinAsync(Guid authenticatedDeviceGuid, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.DeviceCheckinLeases.Where(x => x.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
        var limits = await ReadLimitsAsync(db);
        if (!await db.Devices.AnyAsync(x => x.Guid == authenticatedDeviceGuid && x.ArchivedAtUtc == null, ct) ||
            await db.DeviceCheckinLeases.CountAsync(ct) >= limits.MaxConcurrentCheckins ||
            await db.DeviceCheckinLeases.AnyAsync(x => x.DeviceGuid == authenticatedDeviceGuid, ct)) return null;
        var lease = new DeviceCheckinLease { Id = Guid.NewGuid(), DeviceGuid = authenticatedDeviceGuid, ExpiresAtUtc = now.AddMinutes(2) };
        db.DeviceCheckinLeases.Add(lease);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return lease;
    }

    public async Task EndCheckinAsync(Guid authenticatedDeviceGuid, Guid leaseId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await db.DeviceCheckinLeases.Where(x => x.Id == leaseId && x.DeviceGuid == authenticatedDeviceGuid).ExecuteDeleteAsync(ct);
    }

    public async Task<DeviceTaskOfferDto?> ClaimAsync(Guid authenticatedDeviceGuid, bool regularCheckin, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await RecoverAsync(db, now, ct);
        var limits = await ReadLimitsAsync(db);
        if (!await db.Devices.AnyAsync(x => x.Guid == authenticatedDeviceGuid && x.ArchivedAtUtc == null, ct))
            return null;
        // An unknown outcome still consumes capacity until the agent reports or an operator resolves it.
        var occupied = db.DeviceTasks.Where(x => x.Status == DeviceTaskStatus.Offered || x.Status == DeviceTaskStatus.Running || x.Status == DeviceTaskStatus.Unknown);
        if (await occupied.CountAsync(ct) >= limits.MaxConcurrentTasks || await occupied.AnyAsync(x => x.DeviceGuid == authenticatedDeviceGuid, ct))
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var task = await db.DeviceTasks.Include(x => x.Deployment).Where(x => x.DeviceGuid == authenticatedDeviceGuid && x.Status == DeviceTaskStatus.Queued &&
                                                                              x.Deployment.ExpiresAtUtc > now && (regularCheckin || x.Deployment.Timing == DeviceTaskTiming.Now))
            .OrderBy(x => x.Deployment.CreatedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (task is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        task.Status = DeviceTaskStatus.Offered;
        task.LeaseToken = Guid.NewGuid();
        task.LeaseExpiresAtUtc = now.AddMinutes(2);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(task.Id, task.LeaseToken.Value, task.LeaseExpiresAtUtc.Value, task.Deployment.ActionJson, task.Deployment.ContentHash);
    }

    // An agent must receive a successful acknowledgement BEFORE beginning any side effect.
    public async Task<bool> StartAsync(Guid deviceGuid, Guid taskId, Guid leaseToken, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var task = await db.DeviceTasks.Include(x => x.Deployment).SingleOrDefaultAsync(x => x.Id == taskId && x.DeviceGuid == deviceGuid && x.LeaseToken == leaseToken, ct);
        if (task is null)
            return false;
        if (task.Status == DeviceTaskStatus.Running)
            return true;
        if (task.Status != DeviceTaskStatus.Offered || task.LeaseExpiresAtUtc <= now || task.Deployment.ExpiresAtUtc <= now ||
            !await db.Devices.AnyAsync(x => x.Guid == deviceGuid && x.ArchivedAtUtc == null, ct)) return false;
        if (!(await AgentFeatureSettings.FeaturesAsync(db, ct)).WinGetEnabled)
        {
            using var document = System.Text.Json.JsonDocument.Parse(task.Deployment.ActionJson);
            var action = System.Text.Json.JsonSerializer.Deserialize<CompiledPolicyActionDto>(document.RootElement.GetProperty("Action"));
            if (action is not null && AgentFeatureSettings.UsesWinGet(action))
            {
                task.Status = DeviceTaskStatus.Failed;
                task.FinishedAtUtc = now; task.LeaseExpiresAtUtc = null;
                task.Result = AgentFeatureSettings.WinGetDisabled;
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return false;
            }
        }
        task.Status = DeviceTaskStatus.Running;
        task.StartedAtUtc = now;
        task.LeaseExpiresAtUtc = now.AddMinutes(15);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> HeartbeatAsync(Guid deviceGuid, Guid taskId, Guid leaseToken, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var expiry = clock.GetUtcNow().UtcDateTime.AddMinutes(15);
        return await db.DeviceTasks.Where(x => x.Id == taskId && x.DeviceGuid == deviceGuid && x.LeaseToken == leaseToken &&
                                               (x.Status == DeviceTaskStatus.Running || x.Status == DeviceTaskStatus.Unknown)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, DeviceTaskStatus.Running).SetProperty(x => x.LeaseExpiresAtUtc, expiry), ct) == 1;
    }

    public async Task<bool> CompleteAsync(Guid deviceGuid, Guid taskId, Guid leaseToken, bool success, string? result, CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var task = await db.DeviceTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.DeviceGuid == deviceGuid && x.LeaseToken == leaseToken, ct);
        if (task is null)
            return false;
        var status = success ? DeviceTaskStatus.Succeeded : DeviceTaskStatus.Failed;
        if (task.Status == status)
            return true; // Repeated acknowledgement is harmless.
        if (task.Status is not (DeviceTaskStatus.Running or DeviceTaskStatus.Unknown))
            return false;
        task.Status = status;
        task.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        task.LeaseExpiresAtUtc = null;
        task.Result = result?[..Math.Min(result.Length, 4000)];
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> InterruptAsync(Guid deviceGuid, Guid taskId, Guid leaseToken, string detail, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var task = await db.DeviceTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.DeviceGuid == deviceGuid && x.LeaseToken == leaseToken, ct);
        if (task is null || task.Status is not (DeviceTaskStatus.Running or DeviceTaskStatus.Unknown))
            return false;
        task.Status = DeviceTaskStatus.Unknown;
        task.Result = detail[..Math.Min(detail.Length, 4000)];
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task MaintainAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        await RecoverAsync(db, clock.GetUtcNow().UtcDateTime, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task RecoverAsync(CloneDeployDbContext db, DateTime now, CancellationToken ct)
    {
        await db.DeviceTasks.Where(x => (x.Status == DeviceTaskStatus.Queued || x.Status == DeviceTaskStatus.Offered) &&
                                        (x.Deployment.ExpiresAtUtc <= now || !db.Devices.Any(d => d.Id == x.DeviceId && d.Guid == x.DeviceGuid && d.ArchivedAtUtc == null)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeviceTaskStatus.Cancelled).SetProperty(x => x.FinishedAtUtc, now)
                .SetProperty(x => x.Result, "Expired or device unavailable.").SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null), ct);
        await db.DeviceTasks.Where(x => x.Status == DeviceTaskStatus.Offered && x.LeaseExpiresAtUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeviceTaskStatus.Queued).SetProperty(x => x.LeaseToken, (Guid?)null)
                .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null), ct);
        await db.DeviceTasks.Where(x => x.Status == DeviceTaskStatus.Running && x.LeaseExpiresAtUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeviceTaskStatus.Unknown)
                .SetProperty(x => x.Result, "Agent heartbeat expired. Execution may still be running; no automatic retry."), ct);
    }
}
