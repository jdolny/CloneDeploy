using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Retries moving completed boot images to shared storage in the background.
/// </summary>
public sealed class WinPeTransferWorker(IServiceScopeFactory scopes, BuildStorage local, WinPeArtifactStorage artifacts,
    ILogger<WinPeTransferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>();
                await using var db = await factory.CreateContextAsync();
                var pending = await db.WinPeBuilds.AsNoTracking().Where(x => x.StoredAtUtc == null && x.StorageError == null)
                    .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).ToListAsync(stoppingToken);
                foreach (var id in pending)
                    if (local.Status(id).State == "Ready") await artifacts.TransferAsync(factory, id, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<WinPeVariantActivation>().ActivatePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "WinPE storage transfer failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
