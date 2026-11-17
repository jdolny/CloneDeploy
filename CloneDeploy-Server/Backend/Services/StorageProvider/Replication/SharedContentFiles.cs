using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SharedContentFiles(StorageContentStorage storage)
{
    public ReplicationTransfers? Transfers { get; init; }
    public string? BootRoot { get; init; }
    public static bool Overlaps(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var leftPrefix = Path.EndsInDirectorySeparator(left) ? left : left + Path.DirectorySeparatorChar;
        var rightPrefix = Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar;
        return left.Equals(right, comparison) || left.StartsWith(rightPrefix, comparison) || right.StartsWith(leftPrefix, comparison);
    }

    public string ContentPath(int provider, SharedContent content)
    {
        var root = storage.GetRoot(provider);
        if (BootRoot is not null && (Overlaps(root, BootRoot) ||
            new DirectoryInfo(root).ResolveLinkTarget(true) is { } target && Overlaps(target.FullName, BootRoot)))
            throw new IOException("Shared replica storage must not overlap the public TFTP directory.");
        var relative = content.ReplicaRelativePath;
        CloneDeploy.SiteServices.ReplicaLayout.ValidateRelative(relative);
        return CheckedPath(root, Path.Combine(root, "replicas", "shared", relative));
    }

    public async Task<string> SourceAsync(SharedContent content, CancellationToken ct)
    {
        if (content.SourceProviderId is not { } provider)
            return CheckedPath(Path.GetPathRoot(Path.GetFullPath(content.SourcePath))!, content.SourcePath);
        await storage.EnsureReadyAsync(provider, ct);
        var root = storage.GetRoot(provider);
        return CheckedPath(root, Path.Combine(root, content.SourcePath));
    }

    public async Task CopyAsync(SharedContent content, int provider, Func<Task> verifying, CancellationToken ct)
    {
        if (content.SourceProviderId == provider) throw new IOException("The primary does not need a replica.");
        await storage.EnsureReadyAsync(provider, ct);
        var final = ContentPath(provider, content);
        var partial = CheckedPath(storage.GetRoot(provider), final + ".partial");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (File.Exists(final)) { await verifying(); await VerifyAsync(final, content, ct); return; }
        var source = await SourceAsync(content, ct);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (input.Length != content.SizeBytes) throw new IOException("Shared source size changed.");
            if (Transfers is { } transfers) await transfers.CopyAsync(input, output, ct);
            else await input.CopyToAsync(output, 1048576, ct);
            await output.FlushAsync(ct);
            output.Flush(true);
        }
        await verifying();
        await VerifyAsync(partial, content, ct);
        File.Move(partial, final);
    }

    private static async Task VerifyAsync(string path, SharedContent content, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != content.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(content.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Shared content verification failed.");
    }

    public async Task DeleteContentAsync(SharedContentReplica copy, SharedContent content, CancellationToken ct)
    {
        await storage.EnsureReadyAsync(copy.StorageProviderId, ct);
        var root = storage.GetRoot(copy.StorageProviderId);
        var path = ContentPath(copy.StorageProviderId, content);
        using (var probe = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) _ = probe.MoveNext();
        if (File.Exists(path)) File.Delete(path);
        var partial = CheckedPath(root, path + ".partial");
        if (File.Exists(partial)) File.Delete(partial);
        // Sibling replicas share the directory; cleanup owns only these two files.
    }

    public static string CheckedPath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, comparison)) throw new IOException("Shared content path is outside its root.");
        for (var current = path; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
        {
            if (current is null) throw new IOException("Invalid shared content path.");
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Shared content paths cannot contain links beneath their root."); }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
        return path;
    }
}
