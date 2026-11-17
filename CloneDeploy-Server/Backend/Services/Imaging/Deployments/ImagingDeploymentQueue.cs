using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentQueueDto(bool Admitted, int Position, int Active, int Limit);

public sealed partial class ImagingDeploymentClaims
{
    public const string DeploymentLimitKey = "Imaging.MaxConcurrentDeployments";
    public const int DefaultDeploymentLimit = 4;

    public static async Task<int> DeploymentLimitAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var value = await db.AppSettings.Where(x => x.Name == DeploymentLimitKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return int.TryParse(value, out var limit) && limit is >= 0 and <= 1000 ? limit : DefaultDeploymentLimit;
    }

    // Admission and start are one transaction. Lost admission responses cannot authorize a second apply.
    public async Task<DeploymentQueueDto> EnterQueueAsync(string? token, CancellationToken ct)
    {
        var ticket = ReadTicket(token) ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Global admission lock across application instances, then the usual device lock.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947234, 0)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {ticket.DeviceId})", ct);
        var deployment = await RequireExecutionAsync(db, token, ct);
        if (deployment.ImagerStartedAtUtc != null) throw new InvalidOperationException("This deployment already started. Do not apply it again.");
        if (await MulticastAsync(db, deployment, ct) is { } multicast)
        {
            if (multicast.State != MulticastState.Waiting || multicast.StartRequested)
                throw new InvalidOperationException("This multicast session has already started.");
            if (multicast.SenderLease is null) return new(false, 1, 0, 0);
            deployment.ImagerStartedAtUtc = DateTime.UtcNow;
            await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, 0, 0, 0);
        }
        var task = await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        task.Status = ImagingStatus.InImagingQueue;
        await db.SaveChangesAsync(ct);
        var now = DateTime.UtcNow;
        var live = Pending(db).Where(x => x.MulticastSessionId == null && x.ImagerFailedAtUtc == null && x.ImagerClaimId != null && x.ImagerClaimExpiresAtUtc > now);
        var active = await live.CountAsync(x => x.ImagerStartedAtUtc != null, ct);
        var limit = await DeploymentLimitAsync(db, ct);
        // Only waiting clients count; offline prepared devices cannot block the queue.
        var waiting = await live.Where(x => x.ImagerStartedAtUtc == null &&
                db.ActiveImagingTasks.Any(t => t.DeploymentId == x.Id && t.Status == ImagingStatus.InImagingQueue))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        var position = waiting.IndexOf(deployment.Id) + 1;
        var admitted = position > 0 && position <= Math.Max(0, limit - active);
        if (admitted)
        {
            deployment.ImagerStartedAtUtc = now;
            task = await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        }
        task.QueuePosition = admitted ? 0 : position;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(admitted, task.QueuePosition, active + (admitted ? 1 : 0), limit);
    }
}
