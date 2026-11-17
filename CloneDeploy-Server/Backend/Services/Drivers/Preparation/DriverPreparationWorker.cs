using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

// One local worker, like ExternalDownloadWorker. A content hash is the durable preparation revision.
/// <summary>
/// Prepares downloaded driver packages for use on devices in the background.
/// </summary>
public sealed class DriverPreparationWorker(IServiceScopeFactory scopes, StoredFileStorage sourceStorage,
    DriverPreparationStorage storage, DriverPackageExtractor extractor, IOptions<DriverPreparationOptions> options,
    ILogger<DriverPreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>();
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    lastCleanup = DateTime.UtcNow;
                    try
                    {
                        await using var cleanup = await factory.CreateContextAsync();
                        var referenced = await cleanup.DriverFiles.Where(x => x.PreparedArchiveId != null).Select(x => x.PreparedArchiveId!.Value).ToListAsync(stoppingToken);
                        var retained = referenced.ToHashSet();
                        var deployments = cleanup.Set<ImagingDeployment>().AsNoTracking()
                            .Where(x => x.ImagingCompletedAtUtc == null && x.CancelledAtUtc == null && x.ImagerFailedAtUtc == null)
                            .Select(x => x.SettingsJson).AsAsyncEnumerable();
                        await foreach (var json in deployments.WithCancellation(stoppingToken))
                            foreach (var archive in JsonSerializer.Deserialize<DeploymentSettingsDto>(json)?.Drivers ?? [])
                                retained.Add(archive.Id);
                        storage.RemoveExpiredArtifacts(retained);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning(exception, "Could not clean expired driver preparation artifacts"); }
                }
                if (!recovered)
                {
                    await using var db = await factory.CreateContextAsync();
                    await db.DriverFiles.Where(x => x.PreparationStatus == DriverPreparationStatus.Extracting)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.PreparationStatus, DriverPreparationStatus.Queued), stoppingToken);
                    recovered = true;
                }
                if (await PrepareNextAsync(factory, stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Driver preparation queue failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<bool> PrepareNextAsync(ICloneDeployDbFactory factory, CancellationToken ct)
    {
        DriverFile? job;
        await using (var db = await factory.CreateContextAsync())
            job = await db.DriverFiles.AsNoTracking().Include(x => x.File)
                .Where(x => x.File.Status == StoredFileStatus.Complete && x.File.Sha256Hash != null &&
                    x.PreparationStatus != DriverPreparationStatus.Extracting &&
                    (x.PreparationStatus == DriverPreparationStatus.Queued || x.AttemptedSourceHash != x.File.Sha256Hash))
                .OrderBy(x => x.StoredFileId).FirstOrDefaultAsync(ct);
        if (job is null) return false;

        var work = storage.CreateWorkDirectory();
        var archiveId = Guid.NewGuid();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.TimeoutMinutes));
        try
        {
            var extension = Path.GetExtension(job.File.Name);
            if (job.CatalogVendor.HasValue && Uri.TryCreate(job.File.Url, UriKind.Absolute, out var uri)) extension = Path.GetExtension(uri.AbsolutePath);
            var input = Path.Combine(work, "package" + extension);
            await sourceStorage.Gate.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateContextAsync();
                var current = await db.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.StoredFileId == job.StoredFileId, ct);
                if (current is null || current.File.Status != StoredFileStatus.Complete || current.File.Sha256Hash != job.File.Sha256Hash ||
                    current.File.Url != job.File.Url || current.CatalogVendor != job.CatalogVendor || current.PreparationStatus == DriverPreparationStatus.Extracting) return true;
                current.PreparationStatus = DriverPreparationStatus.Extracting;
                current.AttemptedSourceHash = job.File.Sha256Hash; current.PreparationError = null;
                await db.SaveChangesAsync(ct);
                // Snapshot while the downloader cannot replace the original file.
                await sourceStorage.EnsureReadyAsync(job.File, timeout.Token);
                await using var source = File.OpenRead(sourceStorage.NamedPath(job.File));
                await using var copy = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(copy, timeout.Token);
            }
            finally { sourceStorage.Gate.Release(); }

            await using (var stream = File.OpenRead(input))
                if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, timeout.Token)), job.File.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The stored package no longer matches its recorded checksum. Download or upload it again.");
            var extracted = Path.Combine(work, "drivers");
            var count = await extractor.ExtractAsync(input, extracted, job.CatalogVendor, job.File.Url, timeout.Token);
            var archive = Path.Combine(work, "prepared.zip");
            await extractor.CreateArchiveAsync(extracted, archive, timeout.Token);
            string hash;
            await using (var stream = File.OpenRead(archive)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, timeout.Token));

            await sourceStorage.Gate.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateContextAsync();
                var current = await db.DriverFiles.Include(x => x.File).SingleOrDefaultAsync(x => x.StoredFileId == job.StoredFileId, ct);
                if (current is null) return true;
                if (current.File.Status != StoredFileStatus.Complete || current.File.Sha256Hash != job.File.Sha256Hash ||
                    current.File.Url != job.File.Url || current.CatalogVendor != job.CatalogVendor)
                {
                    current.PreparationStatus = DriverPreparationStatus.Queued;
                    await db.SaveChangesAsync(ct); return true;
                }
                var providerId = await storage.SelectProviderAsync(timeout.Token);
                var destination = storage.ArchivePath(archiveId, providerId);
                using var publication = await FilePublication.CreateAsync(archive, destination, ct: timeout.Token);
                if (current.PreparedStorageProviderId is null && current.PreparedArchiveId is { } previous && File.Exists(storage.ArchivePath(previous)))
                    File.SetLastWriteTimeUtc(storage.ArchivePath(previous), DateTime.UtcNow);
                current.PreparedStorageProviderId = providerId;
                current.PreparedArchiveId = archiveId; current.PreparedSourceHash = job.File.Sha256Hash;
                current.PreparedSha256 = hash; current.PreparedInfCount = count; current.PreparedAtUtc = DateTime.UtcNow;
                current.PreparationStatus = DriverPreparationStatus.Ready; current.PreparationError = null;
                await db.SaveChangesAsync(ct); publication.Commit();
                // Unreferenced immutable archives receive a 24-hour grace period before cleanup.
            }
            finally { sourceStorage.Gate.Release(); }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Driver preparation failed for file {Id}", job.StoredFileId);
            await sourceStorage.Gate.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateContextAsync();
                var current = await db.DriverFiles.SingleOrDefaultAsync(x => x.StoredFileId == job.StoredFileId);
                if (current is not null)
                {
                    current.PreparationStatus = ct.IsCancellationRequested ? DriverPreparationStatus.Queued : DriverPreparationStatus.Error;
                    current.AttemptedSourceHash = job.File.Sha256Hash;
                    current.PreparationError = exception is OperationCanceledException ? "Driver preparation was interrupted or exceeded its time limit." :
                        exception is InvalidOperationException or InvalidDataException ? exception.Message : "Driver preparation failed. See the server log or retry preparation.";
                    await db.SaveChangesAsync();
                }
            }
            finally { sourceStorage.Gate.Release(); }
        }
        finally
        {
            try
            {
                storage.RemoveWorkDirectory(work);
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not clean driver preparation workspace {Path}", work); }
        }
        return true;
    }
}
