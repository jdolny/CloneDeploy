using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class MulticastSenderWorker(IServiceScopeFactory scopes, ILogger<MulticastSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                running.RemoveAll(t => t.IsCompleted);
                using var scope = scopes.CreateScope();
                try
                {
                    if (running.Count < 8 && await scope.ServiceProvider.GetRequiredService<MulticastQueue>().ClaimAsync(null, ct) is { } job)
                        running.Add(Run(job, ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Unable to claim multicast sender"); }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(running); }
    }

    private async Task Run(MulticastOffer job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<MulticastQueue>();
        try
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
            var versionId = Guid.ParseExact(job.File.Key[6..], "N");
            var version = await db.ImageVersions.SingleAsync(x => x.Id == versionId, ct);
            var storage = scope.ServiceProvider.GetRequiredService<StorageContentStorage>();
            await storage.EnsureReadyAsync(job.File.ProviderId, ct);
            var root = storage.GetRoot(job.File.ProviderId);
            var path = Path.Combine(root, "Images", version.ImageId.ToString(), version.Id.ToString("N"), "image.wim");
            for (var item = path; !string.Equals(item, root, StringComparison.OrdinalIgnoreCase); item = Path.GetDirectoryName(item)!)
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Image path contains a link.");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != job.File.Length) throw new IOException("Multicast image size changed.");
            await UdpcastSender.RunAsync(job, input, (p, cancel) => queue.ReportAsync(null, job.Id, job.Lease, p, cancel), ct);
            await queue.ReportAsync(null, job.Id, job.Lease, new(true, true, null), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Multicast sender {Session} stopped", job.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await queue.ReportAsync(null, job.Id, job.Lease, new(false, false, ex.Message), timeout.Token); }
            catch (Exception) { /* Expired/revoked sender leases cannot publish a result. */ }
        }
    }
}
