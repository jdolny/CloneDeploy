using System.Net;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Singleton; database services live only in short scopes. Authorization remains
// with the existing boot/driver endpoints, before they call this resolver.
public sealed class SharedContentDelivery(IServiceScopeFactory scopes, SharedContentFiles files, IHttpContextAccessor requests,
    ILogger<SharedContentDelivery> logger)
{
    public async Task<bool> HasSiteCopyAsync(SharedContentKind kind, string key, string hash, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<SiteDownloadDelivery>() is { } site && await site.HasSharedAsync(kind, key, hash, ct);
    }

    public Task<Stream> OpenManagementAsync(StoredFile file, Func<Task<Stream>> primary, CancellationToken ct) =>
        OpenAsync(SharedContentKind.ManagementFile, file.Id.ToString(), file.Sha256Hash!, primary,
            requests.HttpContext?.Connection.RemoteIpAddress, ct);

    public async Task<string> RequestBootPathAsync(string source, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var path = Path.GetFullPath(source);
        var content = await db.Set<SharedContent>().AsNoTracking().SingleOrDefaultAsync(x => x.Kind == SharedContentKind.BootFile && x.SourcePath == path && x.Current, ct);
        var info = new FileInfo(path);
        if (content is null || content.ModifiedTicks != info.LastWriteTimeUtc.Ticks || content.SizeBytes != info.Length) return path;
        return await RequestPathAsync(content.Kind, content.LogicalKey, content.Sha256, () => Task.FromResult(path), ct);
    }

    public async Task<string> RequestPathAsync(SharedContentKind kind, string key, string? hash, Func<Task<string>> primary, CancellationToken ct)
    {
        var context = requests.HttpContext;
        if (context is null || hash is not { Length: 64 }) return await primary();
        var content = await FindAsync(kind, key, hash, ct);
        if (content is null) return await primary();
        using (var scope = scopes.CreateScope())
            if (scope.ServiceProvider.GetService<SiteDownloadDelivery>() is { } site &&
                await site.TryOpenAsync("shared-" + content.Id, content.SizeBytes, content.Sha256, SharedContentInventory.PurposeFor(content.Kind), ct) is { } marker)
            { await marker.DisposeAsync(); return SiteDownloadDelivery.PathMarker; }
        var selected = await ReserveAsync(content, context.Connection.RemoteIpAddress, ct);
        if (selected.Lease is not null) context.Response.RegisterForDisposeAsync(selected.Lease);
        return selected.Path;
    }

    public async Task<Stream> OpenAsync(SharedContentKind kind, string key, string hash, Func<Task<Stream>> primary, IPAddress? client, CancellationToken ct)
    {
        var content = await FindAsync(kind, key, hash, ct);
        if (content is null) return await primary();
        using (var scope = scopes.CreateScope())
            if (scope.ServiceProvider.GetService<SiteDownloadDelivery>() is { } site &&
                await site.TryOpenAsync("shared-" + content.Id, content.SizeBytes, content.Sha256, SharedContentInventory.PurposeFor(content.Kind), ct) is { } remote)
                return remote;
        var selected = await ReserveAsync(content, client, ct);
        try
        {
            var stream = new FileStream(selected.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return selected.Lease is null ? stream : new LeasedReadStream(stream, selected.Lease);
        }
        catch { if (selected.Lease is not null) await selected.Lease.DisposeAsync(); throw; }
    }

    public async Task<Stream> OpenBootAsync(string relative, string source, IPAddress client, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var content = await db.Set<SharedContent>().AsNoTracking().SingleOrDefaultAsync(x => x.Kind == SharedContentKind.BootFile && x.LogicalKey == relative && x.Current, ct);
        // Boot generators can replace a file between inventory scans. Serve the
        // new local file until its new immutable identity has been catalogued.
        var info = new FileInfo(source);
        if (content is null || content.ModifiedTicks != info.LastWriteTimeUtc.Ticks || content.SizeBytes != info.Length)
            return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await OpenAsync(content.Kind, content.LogicalKey, content.Sha256,
            () => Task.FromResult<Stream>(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)), client, ct);
    }

    private async Task<SharedContent?> FindAsync(SharedContentKind kind, string key, string hash, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var id = SharedContentInventory.Identity(kind, key, hash);
        return await db.Set<SharedContent>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    private async Task<(string Path, ReadLifetime? Lease)> ReserveAsync(SharedContent content, IPAddress? client, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var purpose = SharedContentInventory.PurposeFor(content.Kind);
        var ids = await db.Set<SharedContentReplica>().Where(x => x.ContentId == content.Id && x.State == ImageReplicaState.Ready &&
            db.StorageProviders.Any(t => t.Id == x.StorageProviderId && t.SiteServiceId == null && t.IsEnabled && (t.Purposes & purpose) != 0))
            .Select(x => x.StorageProviderId).ToListAsync(ct);
        if (content.SourceProviderId is { } primary) ids.Add(primary);
        var selection = scope.ServiceProvider.GetRequiredService<StorageReadSelection>();
        var transfer = requests.HttpContext?.Items.TryGetValue(SiteDownloadDelivery.TransferKey, out var value) == true && value is Guid existing ? existing : Guid.NewGuid();
        while (ids.Count > 0)
        {
            int provider;
            try { provider = await selection.ReserveAsync(transfer, "shared:" + content.Id, purpose, ids, client, TimeSpan.FromMinutes(2), ct); }
            catch (IOException) when (content.SourceProviderId is null) { break; }
            try
            {
                var path = provider == content.SourceProviderId ? await files.SourceAsync(content, ct) : files.ContentPath(provider, content);
                if (new FileInfo(path).Length != content.SizeBytes) throw new IOException("Shared content size changed.");
                return (path, new ReadLifetime(scopes, transfer, logger));
            }
            catch (IOException)
            {
                await selection.ReleaseAsync(transfer, CancellationToken.None);
                ids.Remove(provider);
            }
        }
        if (content.SourceProviderId is not null) throw new IOException("No available provider holds the requested shared content.");
        var local = await files.SourceAsync(content, ct);
        if (new FileInfo(local).Length != content.SizeBytes) throw new IOException("Shared source size changed.");
        return (local, null);
    }

    private sealed class ReadLifetime : IAsyncDisposable, IDisposable
    {
        private readonly IServiceScopeFactory scopes;
        private readonly Guid transfer;
        private readonly ILogger logger;
        private readonly CancellationTokenSource stop = new();
        private readonly Task renewal;
        private int disposed;
        public ReadLifetime(IServiceScopeFactory scopes, Guid transfer, ILogger logger)
        { this.scopes = scopes; this.transfer = transfer; this.logger = logger; renewal = RenewAsync(); }
        private async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<StorageReadSelection>().RenewAsync(transfer, TimeSpan.FromMinutes(2), stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogWarning(ex, "Shared transfer reservation renewal failed"); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await stop.CancelAsync(); await renewal;
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<StorageReadSelection>().ReleaseAsync(transfer, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Shared transfer reservation will expire after release failure"); }
            stop.Dispose();
        }
    }

    private sealed class LeasedReadStream(Stream inner, ReadLifetime lease) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { try { inner.Dispose(); } finally { lease.Dispose(); } } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { try { await inner.DisposeAsync(); } finally { await lease.DisposeAsync(); } GC.SuppressFinalize(this); }
    }
}
