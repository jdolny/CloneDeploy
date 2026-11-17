using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Stages verified, immutable native boot trees. Never modifies an active build.</summary>
public sealed class NativePxePublication(TftpRootDirectory root, WinPeArtifactStorage artifacts)
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<Guid, DateTime> _retired = [];
    private readonly HashSet<Guid> _verified = [];
    private string DirectoryPath => root.WinPePath;
    private string BuildPath(Guid id) => Path.Combine(DirectoryPath, id.ToString("N"));
    private static readonly (string Source, string Target)[] Files =
    [
        ("boot.wim", "boot.wim"), ("boot.sdi", "boot.sdi"),
        ("bootmgr.exe", "bios/bootmgr.exe"), ("pxeboot.com", "bios/pxeboot.com"),
        ("pxeboot.n12", "bios/pxeboot.n12"), ("native-bios.bcd", "bios/BCD"),
        ("bootmgfw.efi", "uefi/bootmgfw.efi"), ("native-uefi.bcd", "uefi/BCD")
    ];
    private sealed record Entry(string Path, long Size, string Hash);
    private static readonly (string Source, string Target)[] Arm64Files =
    [
        ("boot-arm64.wim", "arm64/boot.wim"), ("boot-arm64.sdi", "arm64/boot.sdi"),
        ("bootaa64.efi", "arm64/bootmgfw.efi"), ("native-arm64.bcd", "arm64/BCD")
    ];
    public async Task<bool> IsArm64ReadyAsync(Guid id, CancellationToken ct)
    {
        if (!await IsReadyAsync(id, ct)) return false;
        var manifest = Path.Combine(BuildPath(id), "ready.json");
        BuildStorage.RejectLinks(manifest);
        var entries = JsonSerializer.Deserialize<List<Entry>>(await File.ReadAllTextAsync(manifest, ct));
        return entries is not null && entries.Count == Files.Length + Arm64Files.Length &&
            Arm64Files.All(x => entries.Count(e => e.Path == x.Target) == 1);
    }
    public async Task PrepareAsync(WinPeBuild build, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            root.EnsureExists();
            BuildStorage.RejectLinks(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            var final = BuildPath(build.Id);
            if (Directory.Exists(final))
            {
                if (!await VerifyAsync(build.Id, ct)) throw new IOException("Existing native PXE files failed verification. Restore them or rebuild the boot environment.");
                _verified.Add(build.Id);
                return;
            }
            // Staging is outside the TFTP root so incomplete copies cannot be downloaded.
            var stagingRoot = root.StagingPath;
            BuildStorage.RejectLinks(stagingRoot);
            Directory.CreateDirectory(stagingRoot);
            staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var entries = new List<Entry>();
            var outputs = artifacts.Status(build).Artifacts;
            var selectedFiles = outputs.Any(x => x.Name == "native-arm64.bcd") ? Files.Concat(Arm64Files) : Files;
            foreach (var (source, target) in selectedFiles)
            {
                var artifact = outputs.SingleOrDefault(x => x.Name == source)
                    ?? throw new InvalidOperationException("Rebuild this boot environment with the current builder. Missing native output: " + source);
                var path = await artifacts.PathAsync(build, source, ct);
                var destination = Path.Combine(staging, target);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var input = File.OpenRead(path))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    await input.CopyToAsync(output, ct);
                if (new FileInfo(destination).Length != artifact.Size || await BuildStorage.HashAsync(destination, ct) != artifact.Sha256)
                    throw new IOException("Native PXE artifact checksum mismatch: " + source);
                entries.Add(new(target, artifact.Size, artifact.Sha256));
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "ready.json"), JsonSerializer.Serialize(entries), ct);
            BuildStorage.RejectLinks(final);
            Directory.Move(staging, final);
            staging = null;
            _verified.Add(build.Id);
        }
        finally
        {
            try { if (staging is not null) RemoveTree(staging); }
            finally { _gate.Release(); }
        }
    }
    public async Task<bool> IsReadyAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(BuildPath(id))) { _verified.Remove(id); return false; }
            if (_verified.Contains(id)) return true;
            if (!await VerifyAsync(id, ct)) return false;
            _verified.Add(id); return true;
        }
        finally { _gate.Release(); }
    }
    private async Task<bool> VerifyAsync(Guid id, CancellationToken ct)
    {
        var directory = BuildPath(id);
        BuildStorage.RejectLinks(directory);
        var manifest = Path.Combine(directory, "ready.json");
        BuildStorage.RejectLinks(manifest);
        if (!File.Exists(manifest)) return false;
        var entries = JsonSerializer.Deserialize<List<Entry>>(await File.ReadAllTextAsync(manifest, ct));
        if (entries is null || (entries.Count != Files.Length && entries.Count != Files.Length + Arm64Files.Length)) return false;
        var expected = entries.Count == Files.Length ? Files : Files.Concat(Arm64Files);
        foreach (var (_, target) in expected)
        {
            var matches = entries.Where(x => x.Path == target).ToArray();
            if (matches.Length != 1) return false;
            var entry = matches[0];
            var path = Path.Combine(directory, target);
            BuildStorage.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size || await BuildStorage.HashAsync(path, ct) != entry.Hash) return false;
        }
        return true;
    }
    // Called after swapping reservations. A full day protects delayed boots, including across restarts.
    public async Task CleanupAsync(HashSet<Guid> published, bool transfersActive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            BuildStorage.RejectLinks(DirectoryPath);
            foreach (var directory in Directory.EnumerateDirectories(DirectoryPath))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                if (published.Contains(id)) { _retired.Remove(id); continue; }
                if (!_retired.TryGetValue(id, out var since)) { _retired[id] = Clock.GetUtcNow().UtcDateTime; continue; }
                if (transfersActive || Clock.GetUtcNow().UtcDateTime - since < TimeSpan.FromHours(24)) continue;
                // Deny new readers while deleting; existing TFTP handles make these opens fail.
                var claims = new List<FileStream>();
                try
                {
                    ClaimFiles(directory, claims);
                    RemoveTree(directory); _retired.Remove(id); _verified.Remove(id);
                }
                catch (IOException) { /* Transfer or file operation still in progress; retry later. */ }
                finally { foreach (var claim in claims) claim.Dispose(); }
            }
            var stagingRoot = root.StagingPath;
            if (Directory.Exists(stagingRoot))
            {
                BuildStorage.RejectLinks(stagingRoot);
                foreach (var staging in Directory.EnumerateDirectories(stagingRoot))
                    if (Guid.TryParseExact(Path.GetFileName(staging), "N", out _) &&
                        Clock.GetUtcNow().UtcDateTime - Directory.GetLastWriteTimeUtc(staging) > TimeSpan.FromHours(24))
                        RemoveTree(staging);
            }

        }
        finally { _gate.Release(); }
    }
    private static void ClaimFiles(string directory, List<FileStream> claims)
    {
        BuildStorage.RejectLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            BuildStorage.RejectLinks(file);
            claims.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete));
        }
        foreach (var child in Directory.EnumerateDirectories(directory)) ClaimFiles(child, claims);
    }
    private static void RemoveTree(string directory)
    {
        BuildStorage.RejectLinks(directory);
        // Walk without following links, validating each child before descending.
        foreach (var file in Directory.EnumerateFiles(directory)) { BuildStorage.RejectLinks(file); File.Delete(file); }
        foreach (var child in Directory.EnumerateDirectories(directory)) RemoveTree(child);
        Directory.Delete(directory);
    }
}
