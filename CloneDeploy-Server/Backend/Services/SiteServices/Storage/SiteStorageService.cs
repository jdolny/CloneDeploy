using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// The controller authenticates the site's own TLS identity before calling this service.
public sealed class SiteStorageService(ICloneDeployDbFactory factory, SharedContentFiles sharedFiles, StorageContentStorage storage, ISecretProtector secrets)
{
    public async Task<SiteStorageManifest> ManifestAsync(Guid site, CancellationToken ct, bool smb = false, bool linuxMount = false)
    {
        await using var db = await factory.CreateContextAsync();
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).StorageEnabled) throw new UnauthorizedAccessException("Storage is disabled at this site.");
        var rows = await db.StorageProviders.AsNoTracking().Include(x => x.ManagedCredential)
            .Where(x => x.SiteServiceId == site && (x.Type == StorageProviderType.Local || smb && x.Type == StorageProviderType.Smb && (x.LinuxMountPath == null || linuxMount))).ToArrayAsync(ct);
        var providers = rows.Select(x => new SiteStorageProvider(x.Id, x.Path, x.IsEnabled)
        {
            Smb = x.Type == StorageProviderType.Smb, LinuxMountPath = x.LinuxMountPath,
            Username = x.Type == StorageProviderType.Smb && x.LinuxMountPath == null ? x.ManagedCredential?.Username : null,
            Password = x.Type == StorageProviderType.Smb && x.LinuxMountPath == null && x.ManagedCredential is { } credential
                ? secrets.Unprotect(credential.PasswordEncrypted) : null
        }).ToArray();
        var ids = providers.Select(x => x.Id).ToArray();
        var shared = await (from r in db.Set<SharedContentReplica>() join c in db.Set<SharedContent>() on r.ContentId equals c.Id
            where ids.Contains(r.StorageProviderId) && (r.RetryAtUtc == null || r.RetryAtUtc <= DateTime.UtcNow)
            select new { r.StorageProviderId, Content = c, r.State }).ToListAsync(ct);
        var images = await (from r in db.Set<ImageReplica>() join v in db.ImageVersions on r.ImageVersionId equals v.Id into versions
            from v in versions.DefaultIfEmpty()
            where ids.Contains(r.StorageProviderId) && (r.RetryAtUtc == null || r.RetryAtUtc <= DateTime.UtcNow)
            select new { r.StorageProviderId, r.ImageVersionId, Size = v == null ? 0 : v.SizeBytes, Hash = v == null ? "" : v.Sha256, FileName = v == null ? "image.wim" : v.FileName, r.State }).ToListAsync(ct);
        var jobs = shared.Select(x => new SiteStorageJob(x.StorageProviderId, "shared-" + x.Content.Id, x.Content.SizeBytes, x.Content.Sha256, x.State.ToString()) { RelativePath = x.Content.ReplicaRelativePath })
            .Concat(images.Select(x => new SiteStorageJob(x.StorageProviderId, "image-" + x.ImageVersionId.ToString("N"), x.Size, x.Hash ?? "", x.State.ToString()) { RelativePath = $"images/{x.ImageVersionId:N}/{x.FileName}" })).ToArray();
        var leases = await db.Set<StorageReadLease>().Where(x => ids.Contains(x.StorageProviderId) && x.ExpiresAtUtc > DateTime.UtcNow)
            .Select(x => new { x.StorageProviderId, x.ContentKey }).ToListAsync(ct);
        var pinned = leases.Select(x => (x.StorageProviderId, x.ContentKey)).ToHashSet();
        return new(providers, jobs.Where(x => x.State != "Deleting" || !pinned.Contains((x.ProviderId, x.Key.Replace("shared-", "shared:").Replace("image-", "image:")))).ToArray());
    }

    public async Task<Stream> OpenAsync(Guid site, int provider, string key, CancellationToken ct)
    {
        if (!SiteStorageProtocol.ValidKey(key)) throw new InvalidDataException("Invalid content key.");
        await using var db = await factory.CreateContextAsync();
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).StorageEnabled) throw new UnauthorizedAccessException("Storage is disabled at this site.");
        var target = await db.StorageProviders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider && x.SiteServiceId == site &&
            x.IsEnabled && (x.Type == StorageProviderType.Local || x.Type == StorageProviderType.Smb), ct) ?? throw new UnauthorizedAccessException();
        if (key.StartsWith("shared-"))
        {
            var id = key[7..];
            var content = await db.Set<SharedContent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Current, ct);
            if (content is null || (target.Purposes & SharedContentInventory.PurposeFor(content.Kind)) == 0 ||
                !await db.Set<SharedContentReplica>().AnyAsync(x => x.ContentId == id && x.StorageProviderId == provider && x.State != ImageReplicaState.Deleting, ct))
                throw new UnauthorizedAccessException();
            return Open(await sharedFiles.SourceAsync(content, ct), content.SizeBytes);
        }
        var versionId = Guid.ParseExact(key[6..], "N");
        var version = await db.ImageVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.ReplicationApproved, ct);
        if (version is null || (target.Purposes & StoragePurpose.Imaging) == 0 ||
            !await db.Set<ImageReplicationTarget>().AnyAsync(x => x.ImageId == version.ImageId && x.StorageProviderId == provider, ct) ||
            !await db.Set<ImageReplica>().AnyAsync(x => x.ImageVersionId == versionId && x.StorageProviderId == provider && x.State != ImageReplicaState.Deleting, ct))
            throw new UnauthorizedAccessException();
        if (version.FileName is not ("image.wim" or "image.ffu")) throw new InvalidDataException("Invalid image file.");
        await storage.EnsureReadyAsync(version.StorageProviderId, ct);
        var root = storage.GetRoot(version.StorageProviderId);
        return Open(SharedContentFiles.CheckedPath(root, Path.Combine(root, "Images", version.ImageId.ToString(), version.Id.ToString("N"), version.FileName)), version.SizeBytes);
    }

    private static Stream Open(string path, long length)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == length) return stream;
        stream.Dispose(); throw new IOException("Source content length changed.");
    }

    public async Task ReportAsync(Guid site, SiteStorageReport report, CancellationToken ct)
    {
        if (!SiteStorageProtocol.ValidKey(report.Key) || report.Error?.Length > 1000 ||
            report.State is not ("Copying" or "Verifying" or "Ready" or "Failed" or "Deleted")) throw new InvalidDataException("Invalid replica report.");
        await using var db = await factory.CreateContextAsync();
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).StorageEnabled) throw new UnauthorizedAccessException("Storage is disabled at this site.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StorageProviderAdministration.LockAsync(db);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var provider = await db.StorageProviders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == report.ProviderId && x.SiteServiceId == site && (x.Type == StorageProviderType.Local || x.Type == StorageProviderType.Smb), ct)
            ?? throw new UnauthorizedAccessException();
        var deleting = report.State == "Deleted";
        var state = deleting ? ImageReplicaState.Deleting : System.Enum.Parse<ImageReplicaState>(report.State);
        var contentKey = report.Key.Replace("shared-", "shared:").Replace("image-", "image:");
        if (deleting && await db.Set<StorageReadLease>().AnyAsync(x => x.ContentKey == contentKey && x.StorageProviderId == provider.Id && x.ExpiresAtUtc > DateTime.UtcNow, ct))
            throw new InvalidDataException("Replica is still in use.");
        if (report.Key.StartsWith("shared-"))
        {
            var id = report.Key[7..];
            var row = await db.Set<SharedContentReplica>().SingleOrDefaultAsync(x => x.ContentId == id && x.StorageProviderId == provider.Id, ct);
            if (row is null) return; // Idempotent deletion/report retry.
            var content = await db.Set<SharedContent>().SingleAsync(x => x.Id == id, ct);
            if (deleting) { if (row.State != ImageReplicaState.Deleting) throw new InvalidDataException("Deletion was not assigned."); db.Remove(row); }
            else
            {
                if (row.State == ImageReplicaState.Deleting && state == ImageReplicaState.Failed) state = ImageReplicaState.Deleting;
                else if (row.State == ImageReplicaState.Deleting || !provider.IsEnabled || !content.Current ||
                    (provider.Purposes & SharedContentInventory.PurposeFor(content.Kind)) == 0) throw new InvalidDataException("Replica assignment changed.");
                ValidateBytes(report, content.SizeBytes, content.Sha256);
                row.State = state; row.Error = report.Error; row.UpdatedAtUtc = DateTime.UtcNow;
                row.RetryAtUtc = report.State == "Failed" ? DateTime.UtcNow.AddMinutes(1) : null;
            }
        }
        else
        {
            var id = Guid.ParseExact(report.Key[6..], "N");
            var row = await db.Set<ImageReplica>().SingleOrDefaultAsync(x => x.ImageVersionId == id && x.StorageProviderId == provider.Id, ct);
            if (row is null) return;
            if (deleting) { if (row.State != ImageReplicaState.Deleting) throw new InvalidDataException("Deletion was not assigned."); db.Remove(row); }
            else if (row.State == ImageReplicaState.Deleting && state == ImageReplicaState.Failed)
            { row.Error = report.Error; row.RetryAtUtc = DateTime.UtcNow.AddMinutes(1); row.UpdatedAtUtc = DateTime.UtcNow; }
            else
            {
                var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.Id == id && x.ReplicationApproved, ct);
                if (row.State == ImageReplicaState.Deleting || !provider.IsEnabled || (provider.Purposes & StoragePurpose.Imaging) == 0 || version is null ||
                    !await db.Set<ImageReplicationTarget>().AnyAsync(x => x.ImageId == row.ImageId && x.StorageProviderId == provider.Id, ct))
                    throw new InvalidDataException("Replica assignment changed.");
                ValidateBytes(report, version.SizeBytes, version.Sha256!);
                row.State = state; row.Error = report.Error; row.UpdatedAtUtc = DateTime.UtcNow;
                row.RetryAtUtc = state == ImageReplicaState.Failed ? DateTime.UtcNow.AddMinutes(1) : null;
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task UpdateReadAsync(Guid site, Guid id, bool release, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).StorageEnabled) throw new UnauthorizedAccessException("Storage is disabled at this site.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var lease = await db.Set<StorageReadLease>().SingleOrDefaultAsync(x => x.Id == id &&
            db.StorageProviders.Any(p => p.Id == x.StorageProviderId && p.SiteServiceId == site), ct);
        if (lease is null || !release && lease.ExpiresAtUtc <= DateTime.UtcNow) throw new UnauthorizedAccessException();
        if (release)
        {
            db.Remove(lease);
            await db.Set<AgentDownloadLease>().Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        }
        else
        {
            lease.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2);
            await db.Set<AgentDownloadLease>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(2)), ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private static void ValidateBytes(SiteStorageReport report, long size, string hash)
    {
        if (report.Length != size || !string.Equals(report.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replica metadata does not match its assignment.");
    }
}
