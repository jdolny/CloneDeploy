using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record StorageMovedFile(string Relative, long Size, string Sha256);

public sealed class StorageMoveWorker(IServiceScopeFactory scopes, ILogger<StorageMoveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<StorageMoveRunner>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Storage move worker will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class StorageMoveRunner(ICloneDeployDbFactory factory, StorageContentStorage storage, StoredFileStorage originals)
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public async Task RunAsync(CancellationToken ct)
    {
        await using var owner = await factory.CreateContextAsync();
        await owner.Database.OpenConnectionAsync(ct);
        // Match replication lock ordering; session locks survive the short metadata
        // transactions but are automatically released on process/connection failure.
        var locks = new List<int>();
        try
        {
            foreach (var key in new[] { 903422, 903420, 903421 })
            {
                if (!await owner.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({key}) AS \"Value\"").SingleAsync(ct)) return;
                locks.Add(key);
            }
            var job = await owner.Set<StorageMove>().AsNoTracking()
                .Where(x => x.State != "Completed" && x.State != "Failed")
                .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (job is null) return;
            await originals.Gate.WaitAsync(ct);
            try
            {
                if (!job.CleanupAfterUtc.HasValue)
                {
                    await MoveAsync(job, ct);
                    job = await owner.Set<StorageMove>().AsNoTracking().SingleAsync(x => x.Id == job.Id, ct);
                }
                await CleanupAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                await owner.Set<StorageMove>().Where(x => x.Id == job.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Failed").SetProperty(x => x.Error, error), ct);
            }
            finally { originals.Gate.Release(); }
        }
        finally
        {
            foreach (var key in locks.AsEnumerable().Reverse())
                await owner.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None);
        }
    }

    private async Task<(string Source, string Destination)> RootsAsync(StorageMove job, CancellationToken ct)
    {
        await storage.EnsureReadyAsync(job.SourceId, ct);
        await storage.EnsureReadyAsync(job.DestinationId, ct);
        var source = storage.GetRoot(job.SourceId);
        var destination = storage.GetRoot(job.DestinationId);
        var realSource = new DirectoryInfo(source).ResolveLinkTarget(true)?.FullName ?? source;
        var realDestination = new DirectoryInfo(destination).ResolveLinkTarget(true)?.FullName ?? destination;
        if (SharedContentFiles.Overlaps(realSource, realDestination))
            throw new IOException("Source and destination storage must not overlap.");
        // Two UNC aliases can name the same directory even though their strings
        // differ. Never later delete the destination through its source alias.
        var probeName = $".clonedeploy-move-probe-{Guid.NewGuid():N}";
        var probe = Path.Combine(source, probeName);
        await using (var marker = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
        {
            try
            {
                await marker.WriteAsync(new byte[] { 1 }, ct);
                await marker.FlushAsync(ct);
                if (File.Exists(Path.Combine(destination, probeName)))
                    throw new IOException("Source and destination resolve to the same directory through different paths.");
            }
            finally { File.Delete(probe); }
        }
        return (source, destination);
    }

    private async Task MoveAsync(StorageMove job, CancellationToken ct)
    {
        var roots = await RootsAsync(job, ct);
        await using var snapshotDb = await factory.CreateContextAsync();
        var snapshot = await StorageContentInventory.ReadAsync(snapshotDb, job.SourceId, job.Purposes, ct);
        if (snapshot.Blockers.Count > 0) throw new IOException(string.Join(" ", snapshot.Blockers));
        var fingerprint = snapshot.Fingerprint;
        var paths = snapshot.Paths().GroupBy(x => x.Relative, PathComparer)
            .ToDictionary(x => x.Key, x => x.First().Hash, PathComparer);
        // Image sidecars (logs and metadata) belong to the version too.
        foreach (var image in snapshot.Images)
        {
            var folder = Resolve(roots.Source, image.RelativePath);
            foreach (var path in EnumerateFiles(roots.Source, folder))
                paths.TryAdd(Path.GetRelativePath(roots.Source, path).Replace('\\', '/'), null);
        }
        var total = checked(paths.Sum(x => new FileInfo(Resolve(roots.Source, x.Key)).Length) * 3);
        await ProgressAsync(job.Id, "Copying", 0, total, ct);
        var copied = new List<StorageMovedFile>();
        long completed = 0;
        foreach (var entry in paths.OrderBy(x => x.Key))
        {
            var source = Resolve(roots.Source, entry.Key);
            var destination = Resolve(roots.Destination, entry.Key);
            var file = await StorageMoveFiles.CopyVerifiedAsync(source, destination, entry.Key, entry.Value, job.Id,
                async (phase, done) => await ProgressAsync(job.Id, phase, completed + done, total, ct), ct);
            copied.Add(file);
            completed += file.Size * 3;
        }
        // No transaction is held during file I/O. The final inventory must still
        // match exactly before any ownership reference is switched.
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StorageProviderAdministration.LockAsync(db);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        await db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE "StoredFiles", "DriverFiles", "ImageVersions", "ImageUploads", "WinPeBuilds",
                "SharedContent", "ImageReplicationTarget" IN SHARE ROW EXCLUSIVE MODE NOWAIT
            """, ct);
        var providers = await db.StorageProviders.Where(x => x.Id == job.SourceId || x.Id == job.DestinationId).ToListAsync(ct);
        if (providers.Count != 2 || providers.Any(x => !x.IsEnabled || x.SiteServiceId != null) ||
            (providers.Single(x => x.Id == job.DestinationId).Purposes & job.Purposes) != job.Purposes)
            throw new IOException("Provider settings changed; retry the move.");
        var current = await StorageContentInventory.ReadAsync(db, job.SourceId, job.Purposes, ct);
        if (current.Fingerprint != fingerprint) throw new IOException("Source content changed during copying. No ownership changed; retry the move.");
        foreach (var image in current.Images)
            await db.Set<ImageReplica>().Where(x => x.ImageVersionId == image.Id && x.StorageProviderId == job.DestinationId).ExecuteDeleteAsync(ct);
        foreach (var target in current.Targets)
        {
            if (!await db.Set<ImageReplicationTarget>().AnyAsync(x => x.ImageId == target.ImageId && x.StorageProviderId == job.DestinationId, ct))
                db.Add(new ImageReplicationTarget { ImageId = target.ImageId, StorageProviderId = job.DestinationId });
            db.Remove(target);
        }
        current.ChangeOwner(job.DestinationId);
        var saved = await db.Set<StorageMove>().SingleAsync(x => x.Id == job.Id, ct);
        saved.FilesJson = JsonSerializer.Serialize(copied);
        saved.BytesCompleted = total;
        saved.BytesTotal = total;
        saved.State = "Cleaning";
        saved.Error = null;
        // Persist the cleanup stage for recovery, then clean immediately after cutover.
        saved.CleanupAfterUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task CleanupAsync(StorageMove job, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var roots = await RootsAsync(job, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StorageProviderAdministration.LockAsync(db);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var current = await StorageContentInventory.ReadAsync(db, job.SourceId, StoragePurpose.All, ct);
        var retained = current.Paths().Select(x => x.Relative).ToHashSet(PathComparer);
        var imageFolders = await db.Set<ImageReplica>().Where(x => x.StorageProviderId == job.SourceId)
            .Select(x => new { x.ImageId, x.ImageVersionId }).ToListAsync(ct);
        foreach (var file in JsonSerializer.Deserialize<List<StorageMovedFile>>(job.FilesJson) ?? [])
        {
            if (retained.Contains(file.Relative) || imageFolders.Any(x => file.Relative.StartsWith($"Images/{x.ImageId}/{x.ImageVersionId:N}/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Moved content is referenced again on the source. Originals have been retained; retry cleanup after resolving those references.");
            var source = Resolve(roots.Source, file.Relative);
            if (!File.Exists(source)) continue;
            await StorageMoveFiles.VerifyAsync(Resolve(roots.Destination, file.Relative), file, ct);
            await StorageMoveFiles.VerifyAsync(source, file, ct);
            File.Delete(source); // Exact journaled file only; never delete a provider tree.
        }
        var saved = await db.Set<StorageMove>().SingleAsync(x => x.Id == job.Id, ct);
        saved.State = "Completed";
        saved.Error = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ProgressAsync(Guid id, string state, long completed, long total, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.Set<StorageMove>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, state).SetProperty(x => x.BytesCompleted, completed)
            .SetProperty(x => x.BytesTotal, total).SetProperty(x => x.Error, (string?)null), ct);
    }

    private static string Resolve(string root, string relative)
    {
        CloneDeploy.SiteServices.ReplicaLayout.ValidateRelative(relative);
        return SharedContentFiles.CheckedPath(root, Path.Combine(root, relative));
    }

    private static IEnumerable<string> EnumerateFiles(string root, string folder)
    {
        SharedContentFiles.CheckedPath(root, folder);
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            SharedContentFiles.CheckedPath(root, path);
            if (Directory.Exists(path))
                foreach (var file in EnumerateFiles(root, path)) yield return file;
            else yield return path;
        }
    }
}

public static class StorageMoveFiles
{
    public static async Task<StorageMovedFile> CopyVerifiedAsync(string source, string destination, string relative, string? expectedHash,
        Guid job, Func<string, long, Task> progress, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = input.Length;
        var hash = await HashAsync(input, done => progress("Checking source", done), ct);
        if (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Source checksum mismatch: {relative}");
        var file = new StorageMovedFile(relative, size, hash);
        if (File.Exists(destination))
        {
            await VerifyAsync(destination, file, ct, done => progress("Verifying", size * 2 + done));
            await progress("Verifying", size * 3);
            return file;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + $".move-{job:N}";
        SharedContentFiles.CheckedPath(Path.GetDirectoryName(destination)!, partial);
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1048576];
                long written = 0;
                var last = DateTime.UtcNow;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    written += count;
                    if (DateTime.UtcNow - last > TimeSpan.FromSeconds(1))
                    {
                        await progress("Copying", size + written);
                        last = DateTime.UtcNow;
                    }
                }
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            await VerifyAsync(partial, file, ct, done => progress("Verifying", size * 2 + done));
            await progress("Verifying", size * 3);
            File.Move(partial, destination);
            return file;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static async Task VerifyAsync(string path, StorageMovedFile file, CancellationToken ct, Func<long, Task>? progress = null)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != file.Size || !(await HashAsync(stream, progress, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Move verification failed: {file.Relative}");
    }

    private static async Task<string> HashAsync(Stream stream, Func<long, Task>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1048576];
        long read = 0;
        var last = DateTime.UtcNow;
        if (progress is not null) await progress(0);
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            read += count;
            if (progress is not null && DateTime.UtcNow - last > TimeSpan.FromSeconds(1))
            {
                await progress(read);
                last = DateTime.UtcNow;
            }
        }
        if (progress is not null) await progress(read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
