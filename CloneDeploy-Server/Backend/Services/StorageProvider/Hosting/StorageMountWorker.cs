using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks configured storage connections in the background.
/// </summary>
public sealed class StorageMountWorker(StorageMountCoordinator mounts, IOptions<StorageMountOptions> options,
    ILogger<StorageMountWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await mounts.CheckAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Storage-provider reconciliation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.CheckIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
