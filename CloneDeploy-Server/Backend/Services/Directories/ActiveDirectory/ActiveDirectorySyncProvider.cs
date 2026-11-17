using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Updates the application's directory records from the latest Active Directory information.
/// </summary>
public sealed class ActiveDirectorySyncProvider(IActiveDirectoryReader reader, ISecretProtector secretProtector,
    ILogger<ActiveDirectorySyncProvider> logger)
    : IDirectorySyncProvider
{
    public DirectoryProvider Provider => DirectoryProvider.ActiveDirectory;

    public async Task<DirectorySyncSummaryDto> SyncAsync(DirectorySource source, CloneDeployDbContext db, CancellationToken ct)
    {
        var settings = DirectorySourceService.ToOptions(source);
        settings.Validate();
        var state = await db.DirectorySyncStates.SingleOrDefaultAsync(x => x.DirectorySourceId == settings.DirectorySourceId, ct);
        settings.ExpectedDomainGuid = state?.DomainObjectGuid;
        var credential = await db.ManagedCredentials.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == settings.ManagedCredentialId, ct)
            ?? throw new InvalidOperationException("The configured AD managed credential does not exist.");
        var snapshot = await reader.ReadAsync(settings, credential.Username,
            secretProtector.Unprotect(credential.PasswordEncrypted), ct);
        if (source.ApplicationAccessOnly) snapshot = snapshot.ForApplicationAccess();
        ct.ThrowIfCancellationRequested();
        if (state?.DomainObjectGuid is { } domainGuid && domainGuid != snapshot.RootObjectGuid)
            throw new InvalidOperationException("This connection points to a different AD domain. Create another connection instead.");
        var existing = await db.DirectoryObjects.Where(x => x.DirectorySourceId == settings.DirectorySourceId).ToListAsync(ct);
        var completedAt = DateTime.UtcNow;
        db.DirectoryObjects.AddRange(ActiveDirectoryService.MergeSnapshot(existing, snapshot, settings.DirectorySourceId, completedAt));
        if (!source.ApplicationAccessOnly)
        {
        // Different directory sources can reconcile the same previously unlinked device.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('directory-device-reconciliation', 0))", ct);
        var devices = await db.Devices.IgnoreQueryFilters().Include(x => x.ComputerSystem).ToListAsync(ct);
        var reconciliation = DirectoryDeviceReconciler.Reconcile(settings.DirectorySourceId, snapshot.Objects, devices);
        db.Devices.AddRange(reconciliation.Additions);
        foreach (var conflict in reconciliation.Conflicts)
            logger.LogWarning("Directory {DirectorySourceId}: device import for {Computer} needs reconciliation; no device was linked or created",
                settings.DirectorySourceId, conflict);
        }
        // The runner owns the transaction: a failed read/save retains the previous edges.
        // Replace this source's direct edges, including removing memberships of missing groups.
        await db.DirectoryGroupMemberships.Where(x => x.DirectorySourceId == settings.DirectorySourceId).ExecuteDeleteAsync(ct);
        db.DirectoryGroupMemberships.AddRange(snapshot.Memberships);
        // Final import phase, before the runner commits inventory and both membership tables together.
        await db.DirectoryEffectiveGroupMemberships.Where(x => x.DirectorySourceId == settings.DirectorySourceId).ExecuteDeleteAsync(ct);
        db.DirectoryEffectiveGroupMemberships.AddRange(
            EffectiveGroupMembershipBuilder.Build(snapshot, settings.DirectorySourceId, ct));
        ct.ThrowIfCancellationRequested();
        if (state is null)
        {
            state = new DirectorySyncState { DirectorySourceId = settings.DirectorySourceId, RootObjectGuid = snapshot.RootObjectGuid };
            db.DirectorySyncStates.Add(state);
        }
        state.LastSuccessfulSyncAtUtc = completedAt;
        state.RootObjectGuid = snapshot.RootObjectGuid;
        state.DomainObjectGuid = snapshot.RootObjectGuid;
        return new DirectorySyncSummaryDto(completedAt, snapshot.Objects.Count, existing.Count(x => x.IsMissing));
    }
}
