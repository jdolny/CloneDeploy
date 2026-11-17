using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class EntraDirectorySyncProvider(IEntraTokenProvider credentials, EntraGraphReader graph,
    ILogger<EntraDirectorySyncProvider>? logger = null) : IDirectorySyncProvider
{
    public DirectoryProvider Provider => DirectoryProvider.EntraId;

    public async Task<DirectorySyncSummaryDto> SyncAsync(DirectorySource source, CloneDeployDbContext db, CancellationToken ct)
    {
        var settings = await db.Set<EntraConnection>().SingleAsync(ct);
        var state = await db.DirectorySyncStates.SingleOrDefaultAsync(x => x.DirectorySourceId == source.Id, ct);
        if (state is not null && state.RootObjectGuid != settings.TenantId)
            throw new InvalidOperationException("Remove the previous Entra directory before changing tenants.");
        var token = await credentials.GetGraphTokenAsync(settings, false, ct);
        var snapshot = await graph.ReadSnapshotAsync(source.Id, settings.TenantId, token, ct, !source.ApplicationAccessOnly);
        var existing = await db.DirectoryObjects.Where(x => x.DirectorySourceId == source.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        db.DirectoryObjects.AddRange(ActiveDirectoryService.MergeSnapshot(existing, snapshot, source.Id, now));
        if (!source.ApplicationAccessOnly)
        {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('directory-device-reconciliation', 0))", ct);
        // Serialize device creation with agent enrollment and imaging registration.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Devices\" IN SHARE ROW EXCLUSIVE MODE", ct);
        var devices = await db.Devices.IgnoreQueryFilters().ToListAsync(ct);
        var reconciliation = DirectoryDeviceReconciler.ReconcileEntra(source.Id, snapshot.Objects, devices);
        db.Devices.AddRange(reconciliation.Additions);
        foreach (var conflict in reconciliation.Conflicts)
            logger?.LogWarning("Entra directory {DirectorySourceId}: device {DeviceName} has an ambiguous or invalid name; no managed device was created or linked", source.Id, conflict);
        }
        await db.DirectoryGroupMemberships.Where(x => x.DirectorySourceId == source.Id).ExecuteDeleteAsync(ct);
        await db.DirectoryEffectiveGroupMemberships.Where(x => x.DirectorySourceId == source.Id).ExecuteDeleteAsync(ct);
        db.DirectoryGroupMemberships.AddRange(snapshot.Memberships);
        db.DirectoryEffectiveGroupMemberships.AddRange(EffectiveGroupMembershipBuilder.Build(snapshot, source.Id, ct));
        if (state is null)
        {
            state = new DirectorySyncState { DirectorySourceId = source.Id, RootObjectGuid = settings.TenantId };
            db.DirectorySyncStates.Add(state);
        }
        // Directory links use Graph object IDs; agent identities retain independent CloneDeploy GUIDs.
        state.LastSuccessfulSyncAtUtc = now;
        return new(now, snapshot.Objects.Count, existing.Count(x => x.IsMissing));
    }
}
