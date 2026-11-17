namespace CloneDeploy_Server.Backend.Services;

public sealed class ImageUploadCleanupWorker(IServiceScopeFactory scopes, ILogger<ImageUploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ImagingUploadService>().CleanupExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not clean expired image uploads; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
