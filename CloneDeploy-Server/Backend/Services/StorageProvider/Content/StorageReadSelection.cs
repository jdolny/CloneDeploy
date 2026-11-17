using System.Net;
using CloneDeploy.SiteServices;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reserves a known content location for an authorized transfer. Does not discover or create replicas.</summary>
public sealed class StorageReadSelection(ICloneDeployDbFactory factory, StorageContentStorage storage, TimeProvider clock)
{
    public IHttpContextAccessor? Requests { get; init; }

    public bool AllowsSite(StorageProvider provider, IPAddress? address)
    {
        var context = Requests?.HttpContext;
        if (context is null || StorageClientNetworks.Match(provider.PreferredClientNetworks, address) < 0 ||
            provider.SiteService is not { State: SiteRegistrationState.Approved, ProxyEnabled: true } site ||
            site.Issuer.IsRevoked || site.CertificateExpiresAtUtc <= DateTime.UtcNow) return false;
        if (TrustedSiteProxy.IsProxied(context))
        {
            var fingerprint = TrustedSiteProxy.StorageFingerprint(context);
            return fingerprint is not null && (site.CertificateSha256 == fingerprint ||
                site.PreviousCertificateSha256 == fingerprint && site.PreviousCertificateUntilUtc > DateTime.UtcNow);
        }
        return context.Request.Headers[SiteDirectDownload.CapabilityHeader] == "1" &&
            !context.Request.Headers.ContainsKey(SiteDirectDownload.BypassHeader) && site.HttpsCertificate is not null &&
            IPAddress.TryParse(site.ListenAddress, out _) && Uri.TryCreate(site.PublicUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    }
    // The ID must belong to an authenticated transfer; candidate IDs must come from
    // server-owned verified content metadata, never a client's provider list.
    public async Task<int> ReserveAsync(Guid transferId, string contentKey, StoragePurpose purpose,
        IReadOnlyCollection<int> verifiedProviderIds, IPAddress? clientAddress, TimeSpan lifetime, CancellationToken ct)
    {
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(contentKey) || contentKey.Length > 200 ||
            purpose is not (StoragePurpose.Imaging or StoragePurpose.DeviceManagement) || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(2))
            throw new ArgumentException("Invalid storage transfer reservation.");
        var ids = verifiedProviderIds.Distinct().ToArray();
        await using var db = await factory.CreateContextAsync();
        var candidates = await db.StorageProviders.AsNoTracking().Include(x => x.SiteService).ThenInclude(x => x!.Issuer)
            .Where(x => ids.Contains(x.Id) && x.IsEnabled && (x.Purposes & purpose) == purpose).ToListAsync(ct);
        var ready = new List<int>();
        foreach (var candidate in candidates)
        {
            if (candidate.SiteServiceId.HasValue)
            {
                if ((await SiteServiceAdministration.ReadProxyAccessAsync(db, candidate.SiteServiceId.Value, ct)).StorageEnabled && candidate.Type == StorageProviderType.Local && AllowsSite(candidate, clientAddress)) ready.Add(candidate.Id);
                continue;
            }
            try { await storage.EnsureReadyAsync(candidate.Id, ct); ready.Add(candidate.Id); }
            catch (IOException) { /* A verified copy can still be temporarily unavailable. */ }
        }
        if (ready.Count == 0) throw new IOException("No reachable provider holds eligible content for this transfer.");

        var current = await db.Set<StorageReadLease>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == transferId, ct);
        if (current is not null && current.ContentKey != contentKey) throw new InvalidOperationException("Transfer content identity changed.");
        // Range requests reuse their reservation without taking the balancing lock
        // or writing load counters for every chunk. Progress/renewal extends it.
        if (current is not null && current.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime && ready.Contains(current.StorageProviderId))
            return current.StorageProviderId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Short, database-only critical section: every reservation sees previous
        // reservations, including concurrent first requests and other processes.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Set<StorageReadLease>().Where(x => x.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
        candidates = await db.StorageProviders.AsNoTracking().Include(x => x.SiteService).ThenInclude(x => x!.Issuer)
            .Where(x => ready.Contains(x.Id) && x.IsEnabled && (x.Purposes & purpose) == purpose).ToListAsync(ct);
        candidates.RemoveAll(x => x.SiteServiceId.HasValue && !AllowsSite(x, clientAddress));
        foreach (var siteId in candidates.Where(x => x.SiteServiceId.HasValue).Select(x => x.SiteServiceId!.Value).Distinct().ToArray())
            if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, siteId, ct)).StorageEnabled) candidates.RemoveAll(x => x.SiteServiceId == siteId);
        var lease = await db.Set<StorageReadLease>().SingleOrDefaultAsync(x => x.Id == transferId, ct);
        if (lease is not null && lease.ContentKey != contentKey) throw new InvalidOperationException("Transfer content identity changed.");
        // Revalidate replicas under the same lock used to withdraw copies. A
        // caller's candidate snapshot can become stale before this transaction.
        if (contentKey.StartsWith("image:", StringComparison.Ordinal) && Guid.TryParseExact(contentKey[6..], "N", out var versionId))
        {
            var primary = await db.ImageVersions.Where(x => x.Id == versionId).Select(x => (int?)x.StorageProviderId).SingleOrDefaultAsync(ct);
            var pinnedProvider = lease?.StorageProviderId;
            var available = await db.Set<ImageReplica>().Where(x => x.ImageVersionId == versionId &&
                (x.State == ImageReplicaState.Ready && db.Set<ImageReplicationTarget>().Any(t => t.ImageId == x.ImageId && t.StorageProviderId == x.StorageProviderId) ||
                 x.State == ImageReplicaState.Deleting && x.StorageProviderId == pinnedProvider))
                .Select(x => x.StorageProviderId).ToListAsync(ct);
            candidates.RemoveAll(x => x.Id != primary && !available.Contains(x.Id));
        }
        if (contentKey.StartsWith("shared:", StringComparison.Ordinal))
        {
            var contentId = contentKey[7..];
            var content = await db.Set<SharedContent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == contentId, ct);
            if (content is null || SharedContentInventory.PurposeFor(content.Kind) != purpose)
                throw new IOException("Shared content purpose does not match this transfer.");
            var pinned = lease?.StorageProviderId;
            var available = await db.Set<SharedContentReplica>().Where(x => x.ContentId == contentId &&
                (x.State == ImageReplicaState.Ready && db.Set<SharedContent>().Any(c => c.Id == contentId && c.Current) &&
                 db.StorageProviders.Any(t => t.Id == x.StorageProviderId && t.IsEnabled && (t.Purposes & purpose) != 0) ||
                 x.State == ImageReplicaState.Deleting && x.StorageProviderId == pinned)).Select(x => x.StorageProviderId).ToListAsync(ct);
            candidates.RemoveAll(x => x.Id != content?.SourceProviderId && !available.Contains(x.Id));
        }
        var selected = candidates.SingleOrDefault(x => x.Id == lease?.StorageProviderId);
        if (selected is null)
        {
            if (candidates.Count == 0) throw new IOException("Storage eligibility changed. Retry the transfer.");
            var bestMatch = candidates.Max(x => StorageClientNetworks.Match(x.PreferredClientNetworks, clientAddress));
            var preferred = candidates.Where(x => StorageClientNetworks.Match(x.PreferredClientNetworks, clientAddress) == bestMatch).ToList();
            var loads = await db.Set<StorageReadLease>().Where(x => x.Id != transferId && x.ExpiresAtUtc > now)
                .GroupBy(x => x.StorageProviderId).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            var minimum = preferred.Min(x => loads.GetValueOrDefault(x.Id));
            var balanced = preferred.Where(x => loads.GetValueOrDefault(x.Id) == minimum).ToList();
            selected = balanced[Random.Shared.Next(balanced.Count)];
        }
        if (lease is null) { lease = new() { Id = transferId, ContentKey = contentKey }; db.Add(lease); }
        lease.StorageProviderId = selected.Id;
        lease.ExpiresAtUtc = now.Add(lifetime);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return selected.Id;
    }

    public async Task RenewAsync(Guid transferId, TimeSpan lifetime, CancellationToken ct)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(2)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        await using var db = await factory.CreateContextAsync();
        var until = clock.GetUtcNow().UtcDateTime.Add(lifetime);
        await db.Set<StorageReadLease>().Where(x => x.Id == transferId && x.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, until), ct);
    }

    public async Task ReleaseAsync(Guid transferId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<StorageReadLease>().Where(x => x.Id == transferId).ExecuteDeleteAsync(ct);
    }
}
