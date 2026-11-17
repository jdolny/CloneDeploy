using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Transport boundary: site services can supply another implementation later.
public interface IImageReplicaFiles
{
    Task CopyAsync(ImageVersion version, int destination, Func<Task> verifying, CancellationToken ct);
    Task DeleteAsync(ImageReplica replica, CancellationToken ct);
}

public sealed class ImageReplicaFiles(StorageContentStorage storage) : IImageReplicaFiles
{
    public ReplicationTransfers? Transfers { get; init; }
    public async Task CopyAsync(ImageVersion version, int destination, Func<Task> verifying, CancellationToken ct)
    {
        if (destination == version.StorageProviderId) throw new IOException("Cannot replicate over the primary copy.");
        if (version.FileName is not ("image.wim" or "image.ffu") || version.SizeBytes <= 0 ||
            version.Sha256 is not { Length: 64 } || !version.Sha256.All(Uri.IsHexDigit))
            throw new IOException("This version needs verified capture metadata before it can replicate.");
        await storage.EnsureReadyAsync(destination, ct);
        var root = storage.GetRoot(destination);
        var directory = Folder(root, version.ImageId, version.Id);
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, version.FileName);
        var partial = final + ".replica-partial";
        CheckPath(root, final);
        CheckPath(root, partial);
        // A crash after rename but before the DB commit leaves a recoverable copy.
        if (File.Exists(final))
        {
            await verifying();
            await VerifyAsync(final, version, ct);
            return;
        }
        await storage.EnsureReadyAsync(version.StorageProviderId, ct);
        var sourceRoot = storage.GetRoot(version.StorageProviderId);
        var source = Path.Combine(Folder(sourceRoot, version.ImageId, version.Id), version.FileName);
        CheckPath(sourceRoot, source);
        // Restart interrupted copies in place. Never stage on the app's system disk.
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (input.Length != version.SizeBytes) throw new IOException("Primary image size differs from its published metadata.");
            if (Transfers is { } transfers) await transfers.CopyAsync(input, output, ct);
            else await input.CopyToAsync(output, 1048576, ct);
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }
        await verifying();
        await VerifyAsync(partial, version, ct);
        ct.ThrowIfCancellationRequested();
        File.Move(partial, final); // Same directory/filesystem; no second full copy.
    }

    private static async Task VerifyAsync(string path, ImageVersion version, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != version.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Replica size or SHA-256 verification failed.");
    }

    public async Task DeleteAsync(ImageReplica replica, CancellationToken ct)
    {
        await storage.EnsureReadyAsync(replica.StorageProviderId, ct);
        var root = storage.GetRoot(replica.StorageProviderId);
        var directory = Folder(root, replica.ImageId, replica.ImageVersionId);
        // Distinguish an already-removed folder (e.g. crash before DB commit)
        // from an inaccessible provider. Directory.Exists alone hides I/O errors.
        using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) _ = entries.MoveNext();
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw new IOException("Replica directory is not a directory.");
        }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        // Only remove files owned by this transport; never recursively follow links.
        foreach (var name in new[] { "image.wim", "image.ffu", "image.wim.replica-partial", "image.ffu.replica-partial" })
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, name);
            CheckPath(root, path);
            File.Delete(path);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
    }

    private static string Folder(string root, int imageId, Guid versionId)
    {
        if (imageId <= 0 || versionId == Guid.Empty) throw new IOException("Invalid replica identity.");
        var path = Path.Combine(root, "Images", imageId.ToString(), versionId.ToString("N"));
        CheckPath(root, path);
        return path;
    }

    private static void CheckPath(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid replica path.");
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Replica paths cannot contain links below the provider root."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
