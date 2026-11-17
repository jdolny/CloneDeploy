using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SharedReplicationRunner(ICloneDeployDbFactory factory, SharedContentInventory inventory,
    SharedContentFiles files, ILogger<SharedReplicationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var owner = await factory.CreateContextAsync();
        await owner.Database.OpenConnectionAsync(ct);
        if (!await owner.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(903421) AS \"Value\"").SingleAsync(ct)) return;
        try
        {
            // Providers retain replicas while disabled; copying and selection require an enabled provider.
            var configured = await owner.StorageProviders.AnyAsync(ct);
            if (configured) await ReconcileAsync(await inventory.ReadAsync(ct), ct);
            else await ReconcileAsync([], ct);
            var jobs = await owner.Set<SharedContentReplica>().AsNoTracking().Where(x => x.State != ImageReplicaState.Ready && owner.StorageProviders.Any(p => p.Id == x.StorageProviderId && p.SiteServiceId == null) &&
                (x.RetryAtUtc == null || x.RetryAtUtc <= DateTime.UtcNow)).OrderBy(x => x.UpdatedAtUtc).Take(64).ToListAsync(ct);
            var json = await owner.AppSettings.Where(x => x.Name == ReplicationSettingsStore.Key).Select(x => x.Value).SingleOrDefaultAsync(ct);
            var limits = ReplicationSettingsStore.Parse<ReplicationOptions>(json) ?? new();
            if (!limits.Valid) throw new InvalidDataException("Invalid replication settings.");
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = limits.SharedFiles, CancellationToken = ct }, async (job, token) =>
            {
                try { await ProcessAsync(job, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Shared replica {Content} on provider {Provider} will retry", job.ContentId, job.StorageProviderId);
                    await using var db = await factory.CreateContextAsync();
                    await db.Set<SharedContentReplica>().Where(x => x.ContentId == job.ContentId && x.StorageProviderId == job.StorageProviderId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, job.State == ImageReplicaState.Deleting ? ImageReplicaState.Deleting : ImageReplicaState.Failed)
                            .SetProperty(x => x.Error, ex.Message.Substring(0, Math.Min(1000, ex.Message.Length)))
                            .SetProperty(x => x.RetryAtUtc, DateTime.UtcNow.AddMinutes(1)).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), token);
                }
            });
            await owner.Set<SharedContent>().Where(x => !x.Current && !owner.Set<SharedContentReplica>().Any(r => r.ContentId == x.Id) &&
                !owner.Set<StorageReadLease>().Any(l => l.ContentKey == "shared:" + x.Id && l.ExpiresAtUtc > DateTime.UtcNow)).ExecuteDeleteAsync(ct);
        }
        finally { await owner.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(903421)", CancellationToken.None); }
    }

    private async Task ReconcileAsync(List<SharedContent> desired, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StorageProviderAdministration.LockAsync(db);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var providers = await db.StorageProviders.Where(x => x.SiteServiceId == null || x.IsEnabled ||
            db.Set<SharedContentReplica>().Any(r => r.StorageProviderId == x.Id)).ToListAsync(ct);
        var targets = desired.ToDictionary(x => x.Id, x => providers.Where(p => (p.Purposes & SharedContentInventory.PurposeFor(x.Kind)) != 0).Select(p => p.Id).ToHashSet());
        desired = desired.Where(x => targets[x.Id].Count > 0).ToList();
        var contents = await db.Set<SharedContent>().ToListAsync(ct);
        var copies = await db.Set<SharedContentReplica>().ToListAsync(ct);
        var byId = contents.ToDictionary(x => x.Id);
        var copyKeys = copies.Select(x => (x.ContentId, x.StorageProviderId)).ToHashSet();
        var ids = desired.Select(x => x.Id).ToHashSet();
        foreach (var content in contents) content.Current = ids.Contains(content.Id);
        foreach (var content in desired)
        {
            var existing = byId.GetValueOrDefault(content.Id);
            if (existing is null) { db.Add(content); contents.Add(content); byId.Add(content.Id, content); }
            else { existing.ModifiedTicks = content.ModifiedTicks; existing.SourcePath = content.SourcePath; existing.SourceProviderId = content.SourceProviderId; }
            foreach (var provider in targets[content.Id].Where(x => x != content.SourceProviderId))
                if (copyKeys.Add((content.Id, provider)))
                {
                    var copy = new SharedContentReplica { ContentId = content.Id, StorageProviderId = provider, UpdatedAtUtc = DateTime.UtcNow };
                    db.Add(copy); copies.Add(copy);
                }
        }
        foreach (var copy in copies.Where(x => (!ids.Contains(x.ContentId) || !targets[x.ContentId].Contains(x.StorageProviderId)) && x.State != ImageReplicaState.Deleting))
        { copy.State = ImageReplicaState.Deleting; copy.RetryAtUtc = null; copy.UpdatedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ProcessAsync(SharedContentReplica job, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var content = await db.Set<SharedContent>().SingleAsync(x => x.Id == job.ContentId, ct);
        if (job.StorageProviderId == content.SourceProviderId) throw new IOException("Refusing replica work against the primary provider.");
        if (job.State == ImageReplicaState.Deleting)
        {
            if (await db.Set<StorageReadLease>().AnyAsync(x => x.ContentKey == "shared:" + job.ContentId && x.StorageProviderId == job.StorageProviderId && x.ExpiresAtUtc > DateTime.UtcNow, ct))
            { await StateAsync(job, ImageReplicaState.Deleting, ct); return; }
            await files.DeleteContentAsync(job, content, ct);
            await db.Set<SharedContentReplica>().Where(x => x.ContentId == job.ContentId && x.StorageProviderId == job.StorageProviderId).ExecuteDeleteAsync(ct);
            return;
        }
        var purpose = SharedContentInventory.PurposeFor(content.Kind);
        if (!content.Current || !await db.StorageProviders.AnyAsync(x => x.Id == job.StorageProviderId && x.SiteServiceId == null && (x.Purposes & purpose) != 0, ct)) return;
        if (!await db.StorageProviders.AnyAsync(x => x.Id == job.StorageProviderId && x.IsEnabled && x.SiteServiceId == null && (x.Purposes & purpose) != 0, ct))
            throw new IOException("Shared replication destination is unavailable or disabled.");
        await StateAsync(job, ImageReplicaState.Copying, ct);
        await files.CopyAsync(content, job.StorageProviderId, () => StateAsync(job, ImageReplicaState.Verifying, ct), ct);
        await StateAsync(job, ImageReplicaState.Ready, ct);
    }

    private async Task StateAsync(SharedContentReplica job, ImageReplicaState state, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<SharedContentReplica>().Where(x => x.ContentId == job.ContentId && x.StorageProviderId == job.StorageProviderId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.Error, (string?)null).SetProperty(x => x.RetryAtUtc, (DateTime?)null), ct);
    }
}

public sealed class SharedReplicationWorker(IServiceScopeFactory scopes, ILogger<SharedReplicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            do
            {
                try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<SharedReplicationRunner>().RunAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Shared replication will retry; existing copies retained"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
