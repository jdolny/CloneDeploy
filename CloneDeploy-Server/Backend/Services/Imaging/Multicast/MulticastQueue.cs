using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class MulticastQueue(ICloneDeployDbFactory factory)
{
    internal static IQueryable<StorageProvider> Providers(CloneDeployDbContext db, ImageVersion version) => db.StorageProviders.Where(p =>
        p.IsEnabled && (p.Purposes & StoragePurpose.Imaging) != 0 && (p.SiteServiceId == null || p.SiteService!.State == SiteRegistrationState.Approved) &&
        (p.Id == version.StorageProviderId || db.Set<ImageReplica>().Any(r => r.ImageVersionId == version.Id && r.StorageProviderId == p.Id && r.State == ImageReplicaState.Ready)));

    public async Task<MulticastOffer?> ClaimAsync(Guid? site, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (site is { } capabilitySite)
        {
            var capabilities = await SiteServiceAdministration.ReadProxyAccessAsync(db, capabilitySite, ct);
            if (!capabilities.MulticastEnabled || !capabilities.StorageEnabled) throw new UnauthorizedAccessException("Multicast is disabled at this site.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)", ct);
        var now = DateTime.UtcNow;
        var stale = await db.Set<MulticastSession>().Where(x => x.State < MulticastState.Completed &&
            (x.SenderLeaseExpiresAtUtc < now || x.CreatedAtUtc < now.AddHours(-12))).ToListAsync(ct);
        foreach (var old in stale)
        {
            old.State = MulticastState.Failed; old.Error = "Sender stopped reporting or the session expired. Create a new session to retry."; old.FinishedAtUtc = now;
            await EndPendingAsync(db, old.Id, false, ct);
            await db.Set<StorageReadLease>().Where(x => x.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, now.AddMinutes(1)), ct);
        }
        await db.SaveChangesAsync(ct);
        var session = await db.Set<MulticastSession>().Where(x => x.SiteServiceId == site && x.State == MulticastState.Waiting && x.SenderLease == null)
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (session is null) { await tx.CommitAsync(ct); return null; }
        var version = await db.ImageVersions.SingleAsync(x => x.Id == session.ImageVersionId, ct);
        if (!await Providers(db, version).AnyAsync(x => x.Id == session.StorageProviderId && x.SiteServiceId == site, ct))
        {
            session.State = MulticastState.Failed; session.Error = "The selected image copy or sender host is unavailable.";
            await EndPendingAsync(db, session.Id, false, ct);
            session.FinishedAtUtc = now; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        session.SenderLease = Guid.NewGuid(); session.SenderLeaseExpiresAtUtc = now.AddMinutes(2);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(session.Id, session.SenderLease.Value,
            new(session.StorageProviderId, "image-" + version.Id.ToString("N"), version.SizeBytes, version.Sha256!, session.Id, "application/octet-stream", false, version.FileName) { RelativePath = $"images/{version.Id:N}/{version.FileName}" },
            new(session.Id, session.SenderAddress, session.GroupAddress, session.Port), session.ExpectedReceivers, session.MegabitsPerSecond, session.Ttl);
    }

    public async Task<MulticastControl> ReportAsync(Guid? site, Guid id, Guid lease, MulticastProgress progress, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (site is { } capabilitySite)
        {
            var capabilities = await SiteServiceAdministration.ReadProxyAccessAsync(db, capabilitySite, ct);
            if (!capabilities.MulticastEnabled || !capabilities.StorageEnabled) throw new UnauthorizedAccessException("Multicast is disabled at this site.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)", ct);
        var session = await db.Set<MulticastSession>().SingleOrDefaultAsync(x => x.Id == id && x.SiteServiceId == site && x.SenderLease == lease &&
            x.State >= MulticastState.Waiting && x.State < MulticastState.Completed && x.SenderLeaseExpiresAtUtc > DateTime.UtcNow, ct)
            ?? throw new UnauthorizedAccessException("Multicast sender lease expired or was cancelled.");
        if (site is { } siteId && !await db.SiteServices.AnyAsync(x => x.Id == siteId && x.State == SiteRegistrationState.Approved, ct))
            throw new UnauthorizedAccessException();
        session.SenderLeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
        if (progress.Transmitting) session.State = MulticastState.Transmitting;
        if (progress.Complete || progress.Error != null)
        {
            session.State = progress.Error == null ? MulticastState.Completed : MulticastState.Failed;
            session.Error = progress.Error?[..Math.Min(2000, progress.Error.Length)];
            session.FinishedAtUtc = DateTime.UtcNow;
            await EndPendingAsync(db, id, progress.Error == null, ct);
            await db.Set<StorageReadLease>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(session.StartRequested);
    }

    internal static async Task CancelAsync(CloneDeployDbContext db, Guid id, CancellationToken ct)
    {
        // Receiver renewal sees terminal state and stops. Do not change already completed device results.
        await db.Set<MulticastSession>().Where(x => x.Id == id && x.State < MulticastState.Completed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, MulticastState.Cancelled).SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow), ct);
        await EndPendingAsync(db, id, false, ct);
        // Retain the file lease briefly so the sender has time to observe cancellation and close it.
        await db.Set<StorageReadLease>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(1)), ct);
    }

    private static async Task EndPendingAsync(CloneDeployDbContext db, Guid id, bool onlyUnstarted, CancellationToken ct)
    {
        var pending = db.Set<ImagingDeployment>().Where(x => x.MulticastSessionId == id && x.ImagingCompletedAtUtc == null &&
            (!onlyUnstarted || x.ImagerStartedAtUtc == null));
        await pending.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAtUtc, DateTime.UtcNow), ct);
        await db.ActiveImagingTasks.Where(x => x.DeploymentId != null && pending.Any(d => d.Id == x.DeploymentId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ImagingStatus.Cancelled).SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.LastUpdatedAtUtc, DateTime.UtcNow).SetProperty(x => x.StatusMessage, "Multicast session ended. Queue a new deployment to retry."), ct);
    }
}
