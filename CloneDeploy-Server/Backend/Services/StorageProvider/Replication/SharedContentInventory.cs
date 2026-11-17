using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SharedContentInventory(ICloneDeployDbFactory factory, StorageContentStorage storage,
    StoredFileStorage originals, DriverPreparationStorage drivers, IServiceProvider services)
{
    public static StoragePurpose PurposeFor(SharedContentKind kind) =>
        kind == SharedContentKind.ManagementFile ? StoragePurpose.DeviceManagement : StoragePurpose.Imaging;

    public static string Identity(SharedContentKind kind, string key, string hash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{key}:{hash.ToLowerInvariant()}")));

    public static SharedContent Describe(SharedContentKind kind, string key, string name, string hash, long size, int? provider, string path, long modified = 0)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || size < 0) throw new InvalidDataException("Shared content requires verified size and SHA-256 metadata.");
        return new() { Id = Identity(kind, key, hash), Kind = kind, LogicalKey = key, Name = name[..Math.Min(255, name.Length)], Sha256 = hash.ToLowerInvariant(),
            SizeBytes = size, SourceProviderId = provider, SourcePath = path, ModifiedTicks = modified };
    }

    public async Task<List<SharedContent>> ReadAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var previous = await db.Set<SharedContent>().AsNoTracking().ToListAsync(ct);
        var byId = previous.ToDictionary(x => x.Id);
        var knownBuilds = previous.Where(x => x.Kind == SharedContentKind.WinPe && x.LogicalKey.Length > 32).Select(x => x.LogicalKey[..32]).ToHashSet();
        var result = new Dictionary<string, SharedContent>();
        void Add(SharedContent entry) => result[entry.Id] = entry;
        var published = await db.WinPeDefinitions.Where(x => x.PublishedBuildId != null).Select(x => x.PublishedBuildId!.Value).ToListAsync(ct);
        var builds = await db.WinPeBuilds.AsNoTracking().Where(x => x.StoredAtUtc != null && x.StorageProviderId != null).ToListAsync(ct);
        foreach (var build in builds)
        {
            var prefix = build.Id.ToString("N") + "/";
            if (!published.Contains(build.Id) && !knownBuilds.Contains(build.Id.ToString("N"))) continue;
            foreach (var file in JsonSerializer.Deserialize<List<BuildArtifactDto>>(build.StoredArtifactsJson) ?? [])
            {
                // Recipe/log files are administrative records, not boot resources.
                if (file.Name is "request.json" or "build.log") continue;
                StoredFileStorage.ValidateName(file.Name);
                Add(Describe(SharedContentKind.WinPe, prefix + file.Name, file.Name, file.Sha256, file.Size,
                    build.StorageProviderId, $"winpe/{prefix}{file.Name}"));
            }
        }
        var packages = await db.DriverFiles.AsNoTracking().Include(x => x.File).ToListAsync(ct);
        foreach (var package in packages)
        {
            var file = package.File;
            if (file.Status == StoredFileStatus.Complete && file.Size is { } size && file.Sha256Hash is { Length: 64 } hash)
                Add(Describe(SharedContentKind.DriverOriginal, file.Id.ToString(), file.Name, hash, size, file.StorageProviderId,
                    file.StorageProviderId is null ? originals.NamedPath(file) : $"actions/{file.DirectoryGuid:N}/{StoredFileStorage.ValidateName(file.Name)}"));
            if (package.PreparationStatus != DriverPreparationStatus.Ready || package.PreparedArchiveId is not { } id || package.PreparedSha256 is not { Length: 64 } archiveHash) continue;
            var known = byId.GetValueOrDefault(Identity(SharedContentKind.DriverArchive, id.ToString("N"), archiveHash));
            if (known is not null) { known.Current = true; Add(known); continue; }
            try
            {
                if (package.PreparedStorageProviderId is { } provider) await storage.EnsureReadyAsync(provider, ct);
                var path = drivers.ArchivePath(id, package.PreparedStorageProviderId);
                Add(Describe(SharedContentKind.DriverArchive, id.ToString("N"), file.Name + " (prepared)", archiveHash, new FileInfo(path).Length,
                    package.PreparedStorageProviderId, package.PreparedStorageProviderId is null ? path : $"prepared-drivers/{id:N}.zip"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { services.GetService<ILogger<SharedContentInventory>>()?.LogWarning(ex, "Prepared driver {Archive} cannot yet be inventoried; retrying next scan", id); }
        }
        // Queued deployments can still refer to an older prepared archive.
        var snapshots = await db.Set<ImagingDeployment>().Where(x => x.CancelledAtUtc == null && x.ImagingCompletedAtUtc == null && x.ImagerFailedAtUtc == null)
            .Select(x => x.SettingsJson).ToListAsync(ct);
        foreach (var snapshot in snapshots)
            foreach (var driver in JsonSerializer.Deserialize<DeploymentSettingsDto>(snapshot)?.Drivers ?? [])
            {
                var id = Identity(SharedContentKind.DriverArchive, driver.Id.ToString("N"), driver.Sha256);
                if (byId.GetValueOrDefault(id) is { } known) { known.Current = true; Add(known); }
                else if (!result.ContainsKey(id))
                {
                    try
                    {
                        if (driver.StorageProviderId is { } provider) await storage.EnsureReadyAsync(provider, ct);
                        var path = drivers.ArchivePath(driver.Id, driver.StorageProviderId);
                        Add(Describe(SharedContentKind.DriverArchive, driver.Id.ToString("N"), driver.Name, driver.Sha256, new FileInfo(path).Length,
                            driver.StorageProviderId, driver.StorageProviderId is null ? path : $"prepared-drivers/{driver.Id:N}.zip"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { services.GetService<ILogger<SharedContentInventory>>()?.LogWarning(ex, "Assigned driver {Archive} cannot yet be inventoried; retrying next scan", driver.Id); }
                }
            }
        if (services.GetService<SiteBootFiles>() is { } boot && services.GetService<TftpRootDirectory>() is { } root)
        {
            root.EnsureExists();
            // Do not attach a new file's timestamp to a manifest hash captured
            // before an atomic replacement. Keep the older identity for one scan;
            // readers detect its old timestamp and use the newly published source.
            var stamps = Directory.EnumerateFiles(root.RootPath, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Select(path => new FileInfo(path)).ToDictionary(x => Path.GetRelativePath(root.RootPath, x.FullName).Replace('\\', '/'),
                    x => (x.Length, x.LastWriteTimeUtc.Ticks));
            var manifest = await boot.ManifestAsync(ct);
            foreach (var file in manifest.Files)
            {
                // WinPE artifacts already replicate from their owning provider. This
                // published TFTP tree is a boot layout, not another content source;
                // site TFTP servers receive it through SiteBootCache synchronization.
                if (file.Path.StartsWith("WinPe/", StringComparison.OrdinalIgnoreCase)) continue;
                var path = CloneDeploy.SiteServices.SiteBootPaths.Resolve(root.RootPath, file.Path);
                var info = new FileInfo(path);
                if (!stamps.TryGetValue(file.Path, out var before) || before != (info.Length, info.LastWriteTimeUtc.Ticks))
                {
                    foreach (var old in previous.Where(x => x.Kind == SharedContentKind.BootFile && x.LogicalKey == file.Path && x.Current)) Add(old);
                    continue;
                }
                Add(Describe(SharedContentKind.BootFile, file.Path, Path.GetFileName(file.Path), file.Sha256, file.Length,
                    null, path, info.LastWriteTimeUtc.Ticks));
            }
        }
        // Retain complete management files until their stored-file record is removed,
        // including files still referenced by published policies or queued actions.
        var management = await db.StoredFiles.AsNoTracking().Where(x => x.Status == StoredFileStatus.Complete &&
            x.Size != null && x.Sha256Hash != null &&
            (!db.DriverFiles.Any(d => d.StoredFileId == x.Id) || db.ActionFiles.Any(a => a.StoredFileId == x.Id) ||
             db.SoftwareActions.Any(a => a.InstallerFileId == x.Id))).ToListAsync(ct);
        foreach (var file in management.Where(x => x.Sha256Hash is { Length: 64 }))
            Add(Describe(SharedContentKind.ManagementFile, file.Id.ToString(), file.Name, file.Sha256Hash!, file.Size!.Value,
                file.StorageProviderId, file.StorageProviderId is null ? originals.NamedPath(file) :
                    $"actions/{file.DirectoryGuid:N}/{StoredFileStorage.ValidateName(file.Name)}"));
        return result.Values.ToList();
    }
}
