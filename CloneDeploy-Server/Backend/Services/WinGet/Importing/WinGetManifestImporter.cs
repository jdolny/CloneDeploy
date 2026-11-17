using System.Diagnostics;
using System.IO.Compression;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Downloads and imports the WinGet software catalog.
/// </summary>
public sealed class WinGetManifestImporter(
    ICloneDeployDbFactory factory,
    WinGetSettingsService settings,
    IHttpClientFactory clients,
    TimeProvider clock,
    ILogger<WinGetManifestImporter> logger,
    IHostEnvironment environment)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 32L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        int? downloadId = null;
        try
        {
            await using (var featureDb = await factory.CreateContextAsync())
                if (!(await AgentFeatureSettings.FeaturesAsync(featureDb, ct)).WinGetEnabled) throw new InvalidOperationException(AgentFeatureSettings.WinGetDisabled);
            var url = StoredFileStorage.ValidateUrl(await settings.GetSourceUrlAsync(ct));
            await using (var db = await factory.CreateContextAsync())
            {
                // The previous process may have stopped before it could record its failure.
                await db.WinGetManifestDownloads.Where(x => x.Status == ManifestImportStatus.Downloading ||
                        x.Status == ManifestImportStatus.Extracting || x.Status == ManifestImportStatus.Importing)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ManifestImportStatus.Error)
                        .SetProperty(x => x.ErrorMessage, "Import was interrupted before completion."), ct);
                var download = new WinGetManifestDownload
                {
                    Url = url, Status = ManifestImportStatus.Downloading, Progress = "Starting download",
                    DownloadedAtUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
                };
                db.WinGetManifestDownloads.Add(download);
                await db.SaveChangesAsync(ct);
                downloadId = download.Id;
            }

            // Never extract archive paths to disk. The only temporary file is owned by this run.
            await using var zip = new FileStream(Path.Combine(new ServerDataPaths(environment).EnsureTempDirectory(), $"winget-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            using var client = clients.CreateClient("WinGetManifests");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new InvalidDataException("WinGet archive exceeds the 8 GB download limit.");
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var buffer = new byte[81920];
                long received = 0;
                var progress = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    received += read;
                    if (received > MaxArchiveBytes) throw new InvalidDataException("WinGet archive exceeds the 8 GB download limit.");
                    await zip.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    if (progress.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        await ProgressAsync(downloadId.Value, ManifestImportStatus.Downloading, $"Downloaded {received / 1048576:N0} MB", ct);
                        progress.Restart();
                    }
                }
            }
            await zip.FlushAsync(ct);
            zip.Position = 0;
            await ImportArchiveAsync(zip, downloadId.Value, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WinGet manifest import failed");
            if (downloadId.HasValue)
            {
                try
                {
                    using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await using var db = await factory.CreateContextAsync();
                    var message = ex is OperationCanceledException ? "Import canceled or timed out. Previous catalog retained." :
                        $"Import failed. Previous catalog retained. {ex.Message}";
                    await db.WinGetManifestDownloads.Where(x => x.Id == downloadId.Value)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ManifestImportStatus.Error)
                            .SetProperty(x => x.ErrorMessage, message), failureTimeout.Token);
                }
                catch (Exception statusError) { logger.LogError(statusError, "Could not record WinGet import failure"); }
            }
            throw;
        }
        finally { Gate.Release(); }
    }

    private async Task ImportArchiveAsync(Stream zip, int downloadId, CancellationToken ct)
    {
        await ProgressAsync(downloadId, ManifestImportStatus.Extracting, "Reading ZIP manifest entries", ct);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(x =>
            (x.FullName.StartsWith("manifests/", StringComparison.OrdinalIgnoreCase) || x.FullName.Contains("/manifests/", StringComparison.OrdinalIgnoreCase)) &&
            (x.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))).ToList();
        if (entries.Count == 0) throw new InvalidDataException("The ZIP contains no YAML files in a manifests directory.");
        if (entries.Any(x => x.Length > MaxManifestBytes) || entries.Sum(x => x.Length) > MaxExpandedBytes)
            throw new InvalidDataException("Manifest archive exceeds the allowed expanded size.");

        await ProgressAsync(downloadId, ManifestImportStatus.Importing, $"Importing {entries.Count:N0} manifests", ct);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // DELETE is transactional and allows readers to keep seeing the committed catalog during the import.
        await db.WinGetInstallerManifests.ExecuteDeleteAsync(ct);
        await db.WinGetLocaleManifests.ExecuteDeleteAsync(ct);
        await db.WinGetVersionManifests.ExecuteDeleteAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var parser = new WinGetManifestParser();
        var versions = 0;
        var installers = 0;
        var locales = 0;
        var buffer = new byte[8192];
        for (var index = 0; index < entries.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[index];
            try
            {
                await using var source = entry.Open();
                using var content = new MemoryStream();
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (content.Length + read > MaxManifestBytes) throw new InvalidDataException("Manifest exceeds 4 MB.");
                    content.Write(buffer, 0, read);
                }
                content.Position = 0;
                using var reader = new StreamReader(content);
                foreach (var row in parser.Parse(reader))
                {
                    db.Add(row);
                    if (row is WinGetVersionManifest) versions++;
                    if (row is WinGetInstallerManifest) installers++;
                    if (row is WinGetLocaleManifest) locales++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"Could not import {entry.FullName}: {ex.Message}", ex);
            }

            // Flush unconditionally, including when the final manifest belongs to another locale.
            if ((index + 1) % 500 == 0 || index == entries.Count - 1)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                await ProgressAsync(downloadId, ManifestImportStatus.Importing, $"Processed {index + 1:N0} / {entries.Count:N0} manifests", ct);
            }
        }
        if (versions == 0 || installers == 0 || locales == 0)
            throw new InvalidDataException("The archive must include versions, installers, and en-US locale manifests.");

        var summary = $"Imported {versions:N0} versions, {installers:N0} installers, {locales:N0} English locales.";
        await db.WinGetManifestDownloads.Where(x => x.Id == downloadId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ManifestImportStatus.Complete)
                .SetProperty(x => x.Progress, summary).SetProperty(x => x.DownloadedAtUtc, clock.GetUtcNow().UtcDateTime), ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("{Summary}", summary);
    }

    private async Task ProgressAsync(int id, ManifestImportStatus status, string progress, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.WinGetManifestDownloads.Where(x => x.Id == id).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.Status, status).SetProperty(x => x.Progress, progress), ct);
    }
}
