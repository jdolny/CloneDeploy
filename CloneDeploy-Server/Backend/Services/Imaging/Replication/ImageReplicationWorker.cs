using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class ImageReplicationRunner(ICloneDeployDbFactory factory, IImageReplicaFiles files, ILogger<ImageReplicationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Session lock protects file operations without keeping a transaction open
        // during multi-GB copies. A server restart releases it automatically.
        await using var owner = await factory.CreateContextAsync();
        await owner.Database.OpenConnectionAsync(ct);
        if (!await owner.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(903420) AS \"Value\"").SingleAsync(ct)) return;
        try
        {
            await ReconcileAsync(ct);
            var jobs = await owner.Set<ImageReplica>().AsNoTracking()
                .Where(x => x.State != ImageReplicaState.Ready && owner.StorageProviders.Any(p => p.Id == x.StorageProviderId && p.SiteServiceId == null) && (x.RetryAtUtc == null || x.RetryAtUtc <= DateTime.UtcNow))
                .OrderBy(x => x.UpdatedAtUtc).Take(32).ToListAsync(ct);
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                try { await ProcessAsync(job, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Image replica {VersionId} on provider {ProviderId} will retry", job.ImageVersionId, job.StorageProviderId);
                    await using var db = await factory.CreateContextAsync();
                    var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    await db.Set<ImageReplica>().Where(x => x.ImageVersionId == job.ImageVersionId && x.StorageProviderId == job.StorageProviderId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, x => x.State == ImageReplicaState.Deleting ? ImageReplicaState.Deleting : ImageReplicaState.Failed)
                            .SetProperty(x => x.Error, error).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow)
                            .SetProperty(x => x.RetryAtUtc, DateTime.UtcNow.AddMinutes(1)), ct);
                }
            }
        }
        finally
        {
            await owner.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(903420)", CancellationToken.None);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StorageProviderAdministration.LockAsync(db);
        // This short lock also coordinates replica withdrawal with read reservations.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var desired = await (from v in db.ImageVersions
            join t in db.Set<ImageReplicationTarget>() on v.ImageId equals t.ImageId
            where v.ReplicationApproved && v.StorageProviderId != t.StorageProviderId
            select new { v.Id, v.ImageId, t.StorageProviderId }).ToListAsync(ct);
        var keys = desired.Select(x => (x.Id, x.StorageProviderId)).ToHashSet();
        var copies = await db.Set<ImageReplica>().ToListAsync(ct);
        var existing = copies.Select(x => (x.ImageVersionId, x.StorageProviderId)).ToHashSet();
        foreach (var d in desired.Where(x => !existing.Contains((x.Id, x.StorageProviderId))))
            db.Add(new ImageReplica { ImageVersionId = d.Id, ImageId = d.ImageId, StorageProviderId = d.StorageProviderId, UpdatedAtUtc = DateTime.UtcNow });
        foreach (var copy in copies.Where(x => !keys.Contains((x.ImageVersionId, x.StorageProviderId)) && x.State != ImageReplicaState.Deleting))
        {
            copy.State = ImageReplicaState.Deleting;
            copy.RetryAtUtc = null;
            copy.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ProcessAsync(ImageReplica job, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var version = await db.ImageVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.ImageVersionId, ct);
        if (version?.StorageProviderId == job.StorageProviderId) throw new IOException("Replica cleanup cannot remove a primary location.");
        if (job.State == ImageReplicaState.Deleting)
        {
            var key = $"image:{job.ImageVersionId:N}";
            // Deleting copies cannot acquire new leases, but existing transfers
            // remain pinned and may renew until they finish.
            if (await db.Set<StorageReadLease>().AnyAsync(x => x.ContentKey == key && x.StorageProviderId == job.StorageProviderId && x.ExpiresAtUtc > DateTime.UtcNow, ct))
            {
                // Waiting deletions must not monopolize the front of the batch.
                await db.Set<ImageReplica>().Where(x => x.ImageVersionId == job.ImageVersionId && x.StorageProviderId == job.StorageProviderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), ct);
                return;
            }
            await files.DeleteAsync(job, ct);
            await db.Set<ImageReplica>().Where(x => x.ImageVersionId == job.ImageVersionId && x.StorageProviderId == job.StorageProviderId).ExecuteDeleteAsync(ct);
            return;
        }
        if (version is null || !version.ReplicationApproved || !await db.Set<ImageReplicationTarget>().AnyAsync(x => x.ImageId == job.ImageId && x.StorageProviderId == job.StorageProviderId, ct)) return;
        if (!await db.StorageProviders.AnyAsync(x => x.Id == job.StorageProviderId && x.IsEnabled && x.SiteServiceId == null && (x.Purposes & StoragePurpose.Imaging) != 0, ct))
            throw new IOException("Replication destination is disabled or unavailable for main-app imaging transfers.");
        await SetStateAsync(job, ImageReplicaState.Copying, ct);
        await files.CopyAsync(version, job.StorageProviderId, () => SetStateAsync(job, ImageReplicaState.Verifying, ct), ct);
        await SetStateAsync(job, ImageReplicaState.Ready, ct);
    }

    private async Task SetStateAsync(ImageReplica job, ImageReplicaState state, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<ImageReplica>().Where(x => x.ImageVersionId == job.ImageVersionId && x.StorageProviderId == job.StorageProviderId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.Error, (string?)null)
                .SetProperty(x => x.RetryAtUtc, (DateTime?)null).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), ct);
    }
}

public sealed class ImageReplicationWorker(IServiceScopeFactory scopes, ILogger<ImageReplicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ImageReplicationRunner>().RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Image replication will retry"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
