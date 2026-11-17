using System.Security.Cryptography;
using CloneDeploy.SiteServices;

namespace CloneDeploy_Server.Backend.Services;

// Only the existing, public TFTP tree is distributed. No storage-provider or arbitrary-file access.
public sealed class SiteBootFiles(TftpRootDirectory root, PxeBootFileManager bootFiles, NativePxeReservations reservations,
    IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (long Length, long Modified, string Hash)> hashes = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SiteBootManifest> ManifestAsync(CancellationToken ct) => await bootFiles.ReadSnapshotAsync(async () =>
    {
        await gate.WaitAsync(ct);
        try
        {
            root.EnsureExists();
            var files = new List<SiteBootFile>();
            var paths = Directory.EnumerateFiles(root.RootPath, "*", new EnumerationOptions {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });
            long total = 0;
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(path) is ".gitkeep" || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(root.RootPath, path).Replace('\\', '/');
                if (ReplicaStoragePath(relative)) continue;
                SiteBootPaths.Resolve(root.RootPath, relative);
                var info = new FileInfo(path);
                if (info.Length > 8L * 1024 * 1024 * 1024 || (total += info.Length) > 100L * 1024 * 1024 * 1024 || files.Count >= 10000)
                    throw new InvalidDataException("The boot-file collection exceeds the supported size.");
                if (!hashes.TryGetValue(relative, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc.Ticks)
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    cached = (info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(await SHA256.HashDataAsync(input, ct)));
                    hashes[relative] = cached;
                }
                files.Add(new(relative, cached.Length, cached.Hash));
            }
            var present = files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in hashes.Keys.Where(x => !present.Contains(x)).ToArray()) hashes.Remove(old);
            using var scope = scopes.CreateScope();
            var pxe = await scope.ServiceProvider.GetRequiredService<PxeSettingsStore>().ReadAsync(ct);
            // Only advertise native reservations whose complete published tree is in this snapshot.
            var snapshot = reservations.ReadSnapshot();
            var native = snapshot.Targets.Where(x => present.Contains(x.Value.Prefix + "/ready.json"))
                .Select(x => new SiteBootReservation(x.Key.Split('|')[0], x.Value.DefinitionId, x.Value.BuildId) { Architecture = x.Value.Architecture }).ToArray();
            SiteBootDefault? ReadyDefault(NativePxeTarget? target) => target is not null && present.Contains(target.Prefix + "/ready.json")
                ? new(target.DefinitionId, target.BuildId) : null;
            return new SiteBootManifest(files.ToArray(), native, pxe.BiosBootFile, pxe.Efi32BootFile, pxe.Efi64BootFile)
            {
                EfiArm64BootFile = pxe.EfiArm64BootFile, NativeDefault = ReadyDefault(snapshot.Default),
                NativeArm64Default = ReadyDefault(snapshot.Arm64Default), KnownMacs = snapshot.KnownMacs.Order().ToArray()
            };
        }
        finally { gate.Release(); }
    }, ct);

    // A share alias can refer to the public tree despite different path strings.
    // Never re-export replica-owned storage as boot content.
    private static bool ReplicaStoragePath(string relative)
    {
        var parts = relative.Split('/');
        return parts.Zip(parts.Skip(1)).Any(x => x.First.Equals("replicas", StringComparison.OrdinalIgnoreCase) && x.Second.Equals("shared", StringComparison.OrdinalIgnoreCase));
    }

    public FileStream Open(string relative)
    {
        var path = SiteBootPaths.Resolve(root.RootPath, relative);
        if (ReplicaStoragePath(relative)) throw new FileNotFoundException();
        if (Path.GetFileName(path) == ".gitkeep" || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException();
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
    }
}
