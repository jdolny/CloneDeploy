using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Files;

// The database is the queue, so downloads survive tab navigation and application restarts.
// Like ChunkedUploadStore, this worker assumes one application instance owns the local storage.
/// <summary>
/// Downloads externally hosted files in the background and tracks their progress.
/// </summary>
public sealed class ExternalDownloadWorker(IServiceScopeFactory scopes, StoredFileStorage storage,
    IHttpClientFactory clients, ILogger<ExternalDownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Runs on this worker's serial loop, never while a download or
                // publication is active. Also recovers leftovers after a crash.
                if (DateTime.UtcNow - lastCleanup >= TimeSpan.FromMinutes(5))
                {
                    try
                    {
                        foreach (var path in storage.AbandonedDownloadPaths(DateTime.UtcNow.AddHours(-1)))
                            TryDelete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { logger.LogWarning(ex, "Could not clean abandoned external downloads"); }
                    lastCleanup = DateTime.UtcNow;
                }
                using var scope = scopes.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>();
                await using var db = await factory.CreateContextAsync();
                if (!recovered)
                {
                    await db.StoredFiles.Where(x => x.SourceType == FileSourceType.Url && (x.Status == StoredFileStatus.Downloading ||
                        x.Status == StoredFileStatus.CalculatingSha256 || x.Status == StoredFileStatus.CalculatingMd5))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StoredFileStatus.Queued), stoppingToken);
                    recovered = true;
                }
                var next = await db.StoredFiles.AsNoTracking().Where(x => x.SourceType == FileSourceType.Url && x.Status == StoredFileStatus.Queued)
                    .OrderBy(x => x.Id).FirstOrDefaultAsync(stoppingToken);
                if (next is not null)
                {
                    var claimed = await db.StoredFiles.Where(x => x.Id == next.Id && x.Status == StoredFileStatus.Queued)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StoredFileStatus.Downloading)
                            .SetProperty(x => x.Progress, "0%").SetProperty(x => x.LastDownloadAttemptAtUtc, DateTime.UtcNow), stoppingToken);
                    if (claimed == 1)
                    {
                        // An edit/requeue may have happened after the initial queue read.
                        var current = await db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == next.Id, stoppingToken);
                        if (current?.Status == StoredFileStatus.Downloading)
                            await DownloadAsync(factory, current, stoppingToken);
                    }
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "External download queue failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DownloadAsync(ICloneDeployDbFactory factory, StoredFile download, CancellationToken stoppingToken)
    {
        var temporary = storage.NewTemporaryPath();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cancellation.CancelAfter(TimeSpan.FromHours(2));
        var ct = cancellation.Token;
        var monitor = MonitorAsync(factory, download.Id, cancellation);
        try
        {
            using var client = clients.CreateClient("ExternalFiles");
            using var response = await client.GetAsync(StoredFileStorage.ValidateUrl(download.Url), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength > storage.MaxFileSize)
                throw new InvalidOperationException("The download exceeds the configured file size limit.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long total = 0;
            var lastProgress = DateTime.MinValue;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var count = await input.ReadAsync(buffer, readTimeout.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > storage.MaxFileSize) throw new InvalidOperationException("The download exceeds the configured file size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    sha.AppendData(buffer, 0, count);
                    md5.AppendData(buffer, 0, count);
                    if (DateTime.UtcNow - lastProgress >= TimeSpan.FromSeconds(1))
                    {
                        var progress = expectedLength > 0 ? $"{Math.Min(99, total * 100 / expectedLength.Value)}%" : $"{total:N0} bytes";
                        await using var db = await factory.CreateContextAsync();
                        await db.StoredFiles.Where(x => x.Id == download.Id && x.Status == StoredFileStatus.Downloading)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Progress, progress), ct);
                        lastProgress = DateTime.UtcNow;
                    }
                }
            }
            if (total == 0 || (expectedLength.HasValue && total != expectedLength))
                throw new InvalidOperationException("The download is empty or incomplete.");
            var actualHash = Convert.ToHexStringLower(sha.GetHashAndReset());
            var expectedHash = StoredFileStorage.ValidateHash(download.ExpectedSha256Hash);
            if (expectedHash is not null && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SHA-256 mismatch. The downloaded file was discarded.");

            await storage.Gate.WaitAsync(ct);
            try
            {
                await using var db = await factory.CreateContextAsync();
                var current = await db.StoredFiles.SingleOrDefaultAsync(x => x.Id == download.Id, ct);
                if (current?.Status != StoredFileStatus.Downloading) return;
                await storage.EnsureReadyAsync(current, ct);
                var destination = storage.NamedPath(current);
                current.LastCheckedAtUtc = DateTime.UtcNow;
                // A successful check with identical contents must not change the published version.
                if (string.Equals(current.Sha256Hash, actualHash, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                {
                    current.Status = StoredFileStatus.Complete;
                    current.Progress = "100%";
                    current.ErrorMessage = null;
                    await db.SaveChangesAsync(ct);
                    return;
                }
                using var publication = await FilePublication.CreateAsync(temporary, destination, replace: true, ct: ct);
                current.Status = StoredFileStatus.Complete;
                current.Progress = "100%";
                current.ErrorMessage = null;
                current.Sha256Hash = actualHash;
                current.Md5Hash = Convert.ToHexStringLower(md5.GetHashAndReset());
                current.AvailableAtUtc = DateTime.UtcNow;
                current.Size = total;
                await db.SaveChangesAsync(ct);
                publication.Commit();
            }
            finally { storage.Gate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External download {DownloadId} failed", download.Id);
            await using var db = await factory.CreateContextAsync();
            var message = ex is OperationCanceledException ? "Download canceled or timed out." :
                ex is InvalidOperationException ? ex.Message : "Download failed. Check the URL and server connectivity, then retry.";
            await db.StoredFiles.Where(x => x.Id == download.Id && x.Status == StoredFileStatus.Downloading)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, stoppingToken.IsCancellationRequested ? StoredFileStatus.Queued : StoredFileStatus.Error)
                    .SetProperty(x => x.ErrorMessage, message).SetProperty(x => x.Progress, (string?)null));
        }
        finally
        {
            await cancellation.CancelAsync();
            await monitor;
            TryDelete(temporary);
        }
    }

    private static async Task MonitorAsync(ICloneDeployDbFactory factory, int id, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                await using var db = await factory.CreateContextAsync();
                if (!await db.StoredFiles.AnyAsync(x => x.Id == id && x.Status == StoredFileStatus.Downloading, cancellation.Token))
                    await cancellation.CancelAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { await cancellation.CancelAsync(); }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not clean download file {Path}", path); }
        catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "Could not clean download file {Path}", path); }
    }
}
