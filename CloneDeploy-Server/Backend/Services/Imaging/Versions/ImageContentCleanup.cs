using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class ImageContentCleanup(ICloneDeployDbFactory factory, StorageContentStorage storage, ILogger<ImageContentCleanup> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        await using var listDb = await factory.CreateContextAsync();
        var ids = await listDb.Set<ImageContentDeletion>().OrderBy(x => x.QueuedAtUtc).Select(x => x.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                await using var db = await factory.CreateContextAsync();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await StorageProviderAdministration.LockAsync(db);
                var entry = await db.Set<ImageContentDeletion>().SingleOrDefaultAsync(x => x.Id == id, ct);
                if (entry is null) continue;
                if (await db.ImageVersions.AnyAsync(x => x.Id == id, ct) || await db.ImageUploads.AnyAsync(x => x.Id == id, ct))
                    throw new IOException("Image content is still registered; cleanup refused.");
                await storage.EnsureReadyAsync(entry.StorageProviderId, ct);
                var root = Path.GetFullPath(storage.GetRoot(entry.StorageProviderId));
                var target = Path.GetFullPath(Path.Combine(root, "Images", entry.ImageId.ToString(), entry.Id.ToString("N")));
                if (entry.ImageId <= 0 || entry.Id == Guid.Empty || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Invalid image cleanup path.");
                DeleteDirectory(target, root, ct);
                db.Remove(entry);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Image content cleanup {VersionId} failed; will retry", id); }
        }
    }

    private static void DeleteDirectory(string path, string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The provider root is an intentional mount. Reject links at every level beneath it.
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Image cleanup cannot follow links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (!Directory.Exists(path)) { if (File.Exists(path)) File.Delete(path); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteDirectory(child, root, ct);
        Directory.Delete(path, false);
    }
}

public sealed class ImageContentCleanupWorker(IServiceScopeFactory scopes, ILogger<ImageContentCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ImageContentCleanup>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Image content cleanup will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
