using System.IO.Compression;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed class MacFileDeployment(AgentOptions options)
{
    private const int ZipUnixModeShift = 16;

    private const int FileBufferBytes = 65536;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    public async Task<MacScriptResult> RunAsync(AgentPolicyAction action, HttpClient client, Guid runId,
        Guid device, string hash, string contentPath, CancellationToken ct)
    {
        if (action.Type != (int)ActionType.File || action.Condition is not null)
            throw new NotSupportedException("Conditional file deployments are not supported on macOS.");
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid file snapshot hash.");
        var destination = action.Settings.Text("Destination");
        if (!Path.IsPathFullyQualified(destination) || destination.Contains('\0')) throw new InvalidDataException("macOS file destination must be an absolute directory path.");
        var policy = new AgentPolicyContent(0, Guid.NewGuid(), action.Name, 0, JsonSerializer.SerializeToElement(new { }), null, null, null, [action]);
        var snapshot = new AgentPolicySnapshot(1, hash, false, JsonSerializer.SerializeToElement(policy));
        var run = new PolicyRunEntry(runId, device, snapshot, ExecutionState.Running, DateTime.UtcNow, null, null, null, false);
        var cache = new PolicyCache(options);
        await cache.DownloadAsync(client, run, policy, ct, contentPath);
        await CopyAsync(action, cache.DirectoryFor(snapshot, action), destination, ct);
        return new(true, $"{action.Name}: deployed {action.Files.Count} file(s) to {destination}.");
    }

    // macOS system paths such as /tmp and /var are aliases. Resolve only these known aliases;
    // arbitrary symlinks in deployment destinations and archives are rejected.
    internal static string DestinationPath(string path)
    {
        if (OperatingSystem.IsMacOS())
            foreach (var alias in new[] { "/var", "/tmp", "/etc" })
                if (path == alias || path.StartsWith(alias + "/", StringComparison.Ordinal)) { path = "/private" + path; break; }
        return Path.GetFullPath(path);
    }
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Deployment path contains a symbolic link.");
    }
    private static void CheckExistingPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Deployment path contains a symbolic link."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static async Task CopyAsync(AgentPolicyAction action, string source, string destination, CancellationToken ct)
    {
        destination = DestinationPath(destination);
        CheckExistingPath(destination);
        Directory.CreateDirectory(destination);
        var overwrite = action.Settings.Flag("OverwriteExisting");
        foreach (var file in action.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name != Path.GetFileName(file.Name) || file.Name is "." or ".." || file.Name.Contains('\\')) throw new InvalidDataException("Invalid deployment filename.");
            var path = Path.Combine(source, file.Name);
            CheckPath(path);
            if (!await PolicyCache.MatchesAsync(path, file, ct)) throw new InvalidDataException("Cached file changed before deployment.");
            if (action.Settings.Flag("DecompressAfterCopy"))
            {
                using var zip = ZipFile.OpenRead(path);
                var entries = new List<(ZipArchiveEntry Entry, string Path)>();
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                    var root = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
                    if (!target.StartsWith(root, StringComparison.Ordinal) || entry.FullName.Contains('\\') || entry.FullName.Contains(':') ||
                        ((entry.ExternalAttributes >> ZipUnixModeShift) & UnixFileTypeMask) == UnixSymbolicLink)
                        throw new InvalidDataException("Archive contains an unsafe path or symbolic link.");
                    CheckExistingPath(target);
                    entries.Add((entry, target));
                }
                foreach (var (entry, target) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    CheckExistingPath(target);
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open();
                    await WriteAsync(input, target, overwrite, ct);
                }
            }
            else
            {
                var target = Path.Combine(destination, file.Name);
                CheckExistingPath(target);
                if (!overwrite && File.Exists(target)) continue;
                await using var input = File.OpenRead(path);
                await WriteAsync(input, target, overwrite, ct);
            }
        }
    }
    private static async Task WriteAsync(Stream input, string target, bool overwrite, CancellationToken ct)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBufferBytes, true))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            CheckExistingPath(target);
            File.Move(temporary, target, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
