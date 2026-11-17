using System.Diagnostics;

namespace CloneDeploy_Server.Backend.Services;

// One coordinator per main-server process. Slots live exactly as long as their response streams.
public sealed class ReplicationTransfers(ReplicationSettingsStore settings)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, ActiveSite> active = [];
    private readonly Dictionary<Guid, (long Order, long Seen)> waiting = [];
    private readonly Dictionary<Guid, Bucket> siteBuckets = [];
    private readonly Bucket globalBucket = new();
    private sealed class ActiveSite
    {
        public int Images, Shared;
        public long Started = Environment.TickCount64;
    }

    public async Task<IDisposable?> TryEnterAsync(Guid site, bool image, CancellationToken ct)
    {
        var options = await settings.ReadAsync(ct);
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var now = Environment.TickCount64;
            // Pollers that disappear cannot block the queue indefinitely.
            foreach (var id in waiting.Where(x => now - x.Value.Seen > 60000).Select(x => x.Key).ToArray()) waiting.Remove(id);
            var current = active.GetValueOrDefault(site);
            if (current is not null)
            {
                if (active.Count > options.Global.ConcurrentSites) return null;
                // Let an admitted site fill its lanes, then drain it when other sites are waiting.
                if (waiting.Keys.Any(x => x != site) && now - current.Started >= 60000) return null;
                if (image ? current.Images >= 1 : current.Shared >= options.Global.SharedFiles) return null;
            }
            else
            {
                var first = waiting.Count == 0 ? site : waiting.MinBy(x => x.Value.Order).Key;
                if (active.Count >= options.Global.ConcurrentSites || first != site)
                {
                    waiting[site] = (waiting.TryGetValue(site, out var queued) ? queued.Order : now, now);
                    return null;
                }
                waiting.Remove(site);
                active[site] = current = new();
            }
            if (image) current.Images++; else current.Shared++;
            return new Reservation(() =>
            {
                lock (sync)
                {
                    if (image) current.Images--; else current.Shared--;
                    if (current.Images + current.Shared == 0) active.Remove(site);
                }
            });
        }
    }

    public Stream Wrap(Stream input, Guid? site = null, IDisposable? reservation = null) => new LimitedStream(input, this, site, reservation);

    public async Task CopyAsync(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            await WaitAsync(null, count, ct);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    // Debit both budgets together, with no reserved future tokens to leak on cancellation.
    public async ValueTask WaitAsync(Guid? site, int bytes, CancellationToken ct)
    {
        if (bytes is < 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes == 0) return;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var options = await settings.ReadAsync(ct);
            double seconds;
            lock (sync)
            {
                var now = Stopwatch.GetTimestamp();
                var globalRate = (long)options.Global.BandwidthMiB * 1048576;
                globalBucket.Refill(globalRate, now);
                Bucket? local = null;
                var siteRate = site.HasValue ? (long)options.ForSite(site.Value).BandwidthMiB * 1048576 : 0;
                if (site.HasValue && siteRate > 0)
                {
                    if (!siteBuckets.TryGetValue(site.Value, out local)) siteBuckets[site.Value] = local = new();
                    local.Refill(siteRate, now);
                }
                seconds = Math.Max(globalBucket.Delay(bytes, globalRate), local?.Delay(bytes, siteRate) ?? 0);
                if (seconds <= 0)
                {
                    if (globalRate > 0) globalBucket.Tokens -= bytes;
                    if (siteRate > 0) local!.Tokens -= bytes;
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0.001, 1)), ct);
        }
    }

    private sealed class Bucket
    {
        public double Tokens = 65536;
        private long updated = Stopwatch.GetTimestamp();
        public void Refill(long rate, long now)
        {
            Tokens = rate == 0 ? 65536 : Math.Min(65536, Tokens + Stopwatch.GetElapsedTime(updated, now).TotalSeconds * rate);
            updated = now;
        }
        public double Delay(int count, long rate) => rate == 0 ? 0 : Math.Max(0, (count - Tokens) / rate);
    }

    private sealed class Reservation(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }

    private sealed class LimitedStream(Stream input, ReplicationTransfers owner, Guid? site, IDisposable? reservation) : Stream
    {
        private IDisposable? slot = reservation;
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            try
            {
                var count = await input.ReadAsync(buffer[..Math.Min(buffer.Length, 65536)], ct);
                await owner.WaitAsync(site, count, ct);
                if (count == 0) Interlocked.Exchange(ref slot, null)?.Dispose();
                return count;
            }
            catch { Interlocked.Exchange(ref slot, null)?.Dispose(); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Replication streams require asynchronous reads.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { input.Dispose(); } finally { Interlocked.Exchange(ref slot, null)?.Dispose(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await input.DisposeAsync(); } finally { Interlocked.Exchange(ref slot, null)?.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }
}
