using System.Diagnostics;
using System.Security.Cryptography;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.FfuFileSystem.Reading;

/// <summary>
/// Defines and validates FFU read block size, cache capacity, parallelism, and integrity checking.
/// </summary>
internal sealed record FfuReadOptions(int BlockMiB = 4, int CacheMiB = 32, int ParallelReads = 1, bool Verify = true)
{
    private const int MinimumCacheMiB = 32;
    private const int MaximumCacheMiB = 256;
    private const int MaximumParallelReads = 4;
    private const int CacheBlocksPerParallelRead = 2;

    public bool Valid => BlockMiB is 4 or 8 or 16 && CacheMiB is >= MinimumCacheMiB and <= MaximumCacheMiB && ParallelReads is >= 1 and <= MaximumParallelReads &&
        CacheMiB >= BlockMiB * ParallelReads * CacheBlocksPerParallelRead && CacheMiB % BlockMiB == 0;
}

// Bounded cache and read-ahead window. Speculative errors surface only if that block is consumed.
/// <summary>
/// Serves random FFU reads from a bounded HTTPS block cache with read-ahead and optional change detection.
/// </summary>
internal sealed class HttpsFfuReader : IDisposable
{
    private const int BytesPerMiB = 1024 * 1024;
    private const double MinimumRateSampleSeconds = 0.001;

    internal const int BlockSize = 4 * 1024 * 1024; // Native callback buffer cap, independent of HTTP block size.
    private readonly Func<long, int, CancellationToken, Task<byte[]>> fetch;
    private readonly FfuReadOptions options;
    private readonly DiagnosticLog? log;
    private readonly int blockBytes, cacheBlocks;
    private readonly SemaphoreSlim gate = new(1), downloads;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<long, byte[]> hashes = [];
    private readonly Dictionary<long, Task<byte[]>> cache = [];
    private readonly Queue<long> order = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long fetched, requests, hits, misses;
    private int disposed;
    public long Length { get; }
    public HttpsFfuReader(long length, Func<long, int, CancellationToken, Task<byte[]>> fetch, FfuReadOptions? options = null, DiagnosticLog? log = null)
    {
        this.options = options ?? new();
        if (length <= 0 || !this.options.Valid) throw new ArgumentOutOfRangeException(nameof(options));
        Length = length; this.fetch = fetch; this.log = log;
        blockBytes = this.options.BlockMiB * BytesPerMiB;
        cacheBlocks = this.options.CacheMiB / this.options.BlockMiB;
        downloads = new(this.options.ParallelReads);
        log?.Write($"HTTPS FFU tuning: block={this.options.BlockMiB} MiB, cache={this.options.CacheMiB} MiB, parallel={this.options.ParallelReads}, hashes={this.options.Verify}.");
    }
    public async Task<int> ReadAsync(long offset, Memory<byte> target, CancellationToken ct)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ct.ThrowIfCancellationRequested(); stop.Token.ThrowIfCancellationRequested();
        if (offset >= Length || target.Length == 0) return 0;
        using var cancel = ct.Register(Cancel);
        await gate.WaitAsync(stop.Token);
        try
        {
            var wanted = (int)Math.Min(target.Length, Length - offset);
            var copied = 0;
            while (copied < wanted)
            {
                stop.Token.ThrowIfCancellationRequested();
                var block = (offset + copied) / blockBytes;
                var within = (int)((offset + copied) % blockBytes);
                if (cache.ContainsKey(block)) hits++; else misses++;
                // Start the demand read first, then keep the next blocks in flight.
                // Capacity is at least twice the window, so the demand block cannot be evicted here.
                for (var ahead = 0; ahead < options.ParallelReads && (block + ahead) * blockBytes < Length; ahead++)
                {
                    var next = block + ahead;
                    if (cache.ContainsKey(next)) continue;
                    if (cache.Count == cacheBlocks)
                    {
                        var oldest = order.Dequeue();
                        if (oldest == block) { order.Enqueue(oldest); oldest = order.Dequeue(); }
                        var retired = cache[oldest];
                        try { await retired; } catch { /* Unused speculative error is observed. */ }
                        cache.Remove(oldest);
                    }
                    stop.Token.ThrowIfCancellationRequested();
                    cache.Add(next, FetchAsync(next)); order.Enqueue(next);
                }
                var bytes = await cache[block].WaitAsync(stop.Token);
                var count = Math.Min(wanted - copied, bytes.Length - within);
                bytes.AsMemory(within, count).CopyTo(target[copied..]);
                copied += count;
            }
            return copied;
        }
        finally { gate.Release(); }
    }
    private async Task<byte[]> FetchAsync(long block)
    {
        await downloads.WaitAsync(stop.Token);
        try
        {
            var size = (int)Math.Min(blockBytes, Length - block * blockBytes);
            var bytes = await fetch(block * blockBytes, size, stop.Token);
            if (bytes.Length != size) throw new InvalidDataException("FFU read returned an incomplete block.");
            Interlocked.Add(ref fetched, size); Interlocked.Increment(ref requests);
            if (options.Verify)
            {
                var hash = SHA256.HashData(bytes);
                lock (hashes)
                {
                    if (hashes.TryGetValue(block, out var original) && !CryptographicOperations.FixedTimeEquals(hash, original))
                        throw new InvalidDataException("FFU content changed during deployment.");
                    hashes[block] = hash;
                }
            }
            return bytes;
        }
        finally { downloads.Release(); }
    }
    public void Cancel() => stop.Cancel();
    public void ReportStats() => log?.Write($"HTTPS FFU transfer: {Interlocked.Read(ref fetched) / (double)BytesPerMiB:F1} MiB, {Interlocked.Read(ref requests)} requests, {hits} cache hits, {misses} misses, {elapsed.Elapsed.TotalSeconds:F1}s, {Interlocked.Read(ref fetched) / (double)BytesPerMiB / Math.Max(MinimumRateSampleSeconds, elapsed.Elapsed.TotalSeconds):F1} MiB/s over mount lifetime.");
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); gate.Wait();
        try
        {
            try { Task.WhenAll(cache.Values).GetAwaiter().GetResult(); } catch { }
            ReportStats();
            cache.Clear(); hashes.Clear();
        }
        finally { gate.Release(); }
    }
}
