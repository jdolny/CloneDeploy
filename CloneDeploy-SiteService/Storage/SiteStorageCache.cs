using System.Net.Http.Json;
using System.Security.Cryptography;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

// Only explicitly assigned files beneath our private replica namespace may be removed.
public sealed class SiteStorageCache(SiteLocalStore store)
{
    private const int TransferBufferBytes = 1024 * 1024;
    private const int MaximumSharedFileConcurrency = 16;
    private const int MaximumProviders = 10_000;
    private const int MaximumJobs = 100_000;
    private const int MaximumErrorCharacters = 1000;
    private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Ticks, string Hash)> verified = new(StringComparer.Ordinal);
    private Dictionary<int, SiteStorageProvider> configured = [];
    private Dictionary<int, string> unavailable = [];

    public async Task<Stream> OpenReadAsync(Guid site, SiteDownloadOffer offer, CancellationToken ct)
    {
        if (!Volatile.Read(ref configured).TryGetValue(offer.ProviderId, out var provider) || !provider.Enabled)
            throw new IOException("Site provider has not synchronized its configuration.");
        var root = ProviderRoot(provider);
        if (Overlaps(root, store.DirectoryPath) || Overlaps(root, AppContext.BaseDirectory)) throw new IOException("Invalid site storage root.");
        if (Volatile.Read(ref unavailable).TryGetValue(provider.Id, out var error)) throw new IOException(error);
        var path = SiteStorageProtocol.Resolve(root, site, provider.Id, offer.Key, provider.Smb && OperatingSystem.IsWindows(), offer.RelativePath);
        if (!await MatchesAsync(path, new(offer.ProviderId, offer.Key, offer.Length, offer.Sha256, "Ready"), ct))
            throw new IOException("Site replica is missing or failed verification.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferBytes, FileOptions.Asynchronous | (offer.Ranges ? FileOptions.RandomAccess : FileOptions.SequentialScan));
    }

    public async Task SynchronizeAsync(HttpClient http, Guid site, CancellationToken ct)
    {
        var manifest = await http.GetFromJsonAsync<SiteStorageManifest>("/api/site-services/storage/manifest?smb=1&linuxMount=1", ct)
            ?? throw new InvalidDataException("Missing storage manifest.");
        if (manifest.Providers is null || manifest.Jobs is null || manifest.SharedFileConcurrency is < 1 or > MaximumSharedFileConcurrency || manifest.Providers.Length > MaximumProviders || manifest.Jobs.Length > MaximumJobs ||
            manifest.Jobs.Select(x => (x.ProviderId, x.Key)).Distinct().Count() != manifest.Jobs.Length ||
            manifest.Providers.Select(x => x.Id).Distinct().Count() != manifest.Providers.Length)
            throw new InvalidDataException("Invalid storage manifest.");
        var providers = manifest.Providers.ToDictionary(x => x.Id, x => x with { Username = null, Password = null });
        // Validate the entire batch before running concurrent filesystem operations.
        foreach (var job in manifest.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!providers.TryGetValue(job.ProviderId, out var provider) || !SiteStorageProtocol.ValidKey(job.Key) ||
                job.State is not ("Pending" or "Copying" or "Verifying" or "Ready" or "Failed" or "Deleting"))
                throw new InvalidDataException("Invalid storage job.");
            ReplicaLayout.ValidateRelative(job.RelativePath);
            // Validate identity/path agreement before any deletes or concurrent copies.
            SiteStorageProtocol.ValidatePath(job.Key, job.RelativePath);
        }
        var failures = new Dictionary<int, string>();
        foreach (var provider in manifest.Providers.Where(x => x.Smb && (x.Enabled || manifest.Jobs.Any(j => j.ProviderId == x.Id && j.State == "Deleting"))))
        {
            try { await SiteStorageConnection.CheckAsync(provider, site, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { failures[provider.Id] = ex.Message; }
        }
        Volatile.Write(ref unavailable, failures);
        Volatile.Write(ref configured, providers);
        foreach (var job in manifest.Jobs.Where(x => x.State == "Deleting")) await ProcessAsync(job);
        var jobs = manifest.Jobs.Where(x => x.State != "Deleting").OrderBy(x => x.State == "Ready").ToArray();
        await Task.WhenAll(RunLaneAsync(true, 1), RunLaneAsync(false, manifest.SharedFileConcurrency));
        if (failures.Count > 0) throw new IOException(string.Join("; ", failures.Select(x => $"Provider {x.Key}: {x.Value}")));

        async Task RunLaneAsync(bool image, int concurrency)
        {
            var deferred = 0;
            await Parallel.ForEachAsync(jobs.Where(x => x.Key.StartsWith("image-") == image),
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (job, _) =>
                {
                    if (Volatile.Read(ref deferred) != 0) return;
                    if (!await ProcessAsync(job)) Interlocked.Exchange(ref deferred, 1);
                });
        }

        async Task<bool> ProcessAsync(SiteStorageJob job)
        {
            ct.ThrowIfCancellationRequested();
            var provider = providers[job.ProviderId];
            if (!provider.Enabled && job.State != "Deleting") return true;
            try
            {
                var root = ProviderRoot(provider);
                if (Overlaps(root, store.DirectoryPath) || Overlaps(root, AppContext.BaseDirectory))
                    throw new InvalidDataException("Replica storage cannot overlap the site service installation or identity directory.");
                if (failures.TryGetValue(provider.Id, out var error)) throw new IOException(error);
                var path = SiteStorageProtocol.Resolve(root, site, provider.Id, job.Key, provider.Smb && OperatingSystem.IsWindows(), job.RelativePath);
                var partial = path + ".partial";
                SiteBootPaths.RejectLinks(partial);
                if (job.State == "Deleting")
                {
                    // Probe the provider first: an offline disk must not be mistaken for a completed deletion.
                    using (var probe = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) _ = probe.MoveNext();
                    var folder = Path.GetDirectoryName(path)!;
                    var exists = true;
                    try { _ = File.GetAttributes(folder); }
                    catch (DirectoryNotFoundException) { exists = false; }
                    catch (FileNotFoundException) { exists = false; }
                    if (exists)
                    {
                        File.Delete(path); File.Delete(partial);
                        // Version records may already be gone. Both image formats belong to this version.
                        if (job.Key.StartsWith("image-"))
                            foreach (var name in new[] { "image.wim", "image.ffu" })
                            {
                                var imagePath = SiteStorageProtocol.Resolve(root, site, provider.Id, job.Key, provider.Smb && OperatingSystem.IsWindows(), $"images/{job.Key[6..]}/{name}");
                                File.Delete(imagePath);
                                SiteBootPaths.RejectLinks(imagePath + ".partial");
                                File.Delete(imagePath + ".partial");
                                verified.TryRemove(imagePath, out _);
                            }
                    }
                    verified.TryRemove(path, out _);
                    await ReportAsync(http, job, "Deleted", null, ct);
                    return true;
                }
                if (job.Length < 0 || job.Sha256 is not { Length: SiteProxyProtocol.Sha256HexCharacters } || !job.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid storage content metadata.");
                var valid = await MatchesAsync(path, job, ct);
                if (!valid)
                {
                    using var response = await http.GetAsync($"/api/site-services/storage/file?provider={provider.Id}&key={job.Key}", HttpCompletionOption.ResponseHeadersRead, ct);
                    // Capacity is normal backpressure, not a failed replica. Poll again next cycle.
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return false;
                    response.EnsureSuccessStatusCode();
                    await ReportAsync(http, job, "Copying", null, ct);
                    _ = ProviderRoot(provider);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var input = await response.Content.ReadAsStreamAsync(ct))
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var buffer = new byte[TransferBufferBytes]; long total = 0;
                        while (true)
                        {
                            idle.CancelAfter(TransferIdleTimeout);
                            var read = await input.ReadAsync(buffer, idle.Token);
                            if (read == 0) break;
                            total += read;
                            if (total > job.Length) throw new InvalidDataException("Replica exceeded its assigned size.");
                            await output.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                        }
                        if (total != job.Length) throw new InvalidDataException("Replica download was truncated.");
                        await output.FlushAsync(ct); output.Flush(true);
                    }
                    await ReportAsync(http, job, "Verifying", null, ct);
                    verified.TryRemove(partial, out _);
                    if (!await MatchesAsync(partial, job, ct)) throw new InvalidDataException("Replica SHA-256 verification failed.");
                    ct.ThrowIfCancellationRequested(); _ = ProviderRoot(provider); File.Move(partial, path, true);
                    verified.TryRemove(partial, out _);
                    var published = new FileInfo(path);
                    verified[path] = (published.Length, published.LastWriteTimeUtc.Ticks, job.Sha256);
                }
                if (!valid || job.State != "Ready") await ReportAsync(http, job, "Ready", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            { await ReportAsync(http, job, "Failed", "Storage transfer timed out without progress.", ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
            {
                await ReportAsync(http, job, "Failed", ex.Message[..Math.Min(MaximumErrorCharacters, ex.Message.Length)], ct);
            }
            return true;
        }
    }

    private static string ProviderRoot(SiteStorageProvider provider)
    {
        if (!provider.Smb || !OperatingSystem.IsLinux()) return provider.Path;
        LinuxSmbMount.Verify(provider.LinuxMountPath, provider.Path);
        return provider.LinuxMountPath!;
    }

    private async Task<bool> MatchesAsync(string path, SiteStorageJob job, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != job.Length) return false;
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks, job.Sha256);
        if (verified.TryGetValue(path, out var previous) && previous == stamp) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(job.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        verified[path] = stamp; return true;
    }

    private static async Task ReportAsync(HttpClient http, SiteStorageJob job, string state, string? error, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/site-services/storage/report", new SiteStorageReport(job.ProviderId, job.Key, job.Length, job.Sha256, state, error), ct);
        response.EnsureSuccessStatusCode();
    }

    private static bool Overlaps(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)); right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var leftPrefix = Path.EndsInDirectorySeparator(left) ? left : left + Path.DirectorySeparatorChar;
        var rightPrefix = Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar;
        return left.Equals(right, comparison) || left.StartsWith(rightPrefix, comparison) || right.StartsWith(leftPrefix, comparison);
    }
}
