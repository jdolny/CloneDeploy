namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Checks for due background tasks and processes queued tasks one at a time.
/// </summary>
/// <remarks>
/// The background scheduler that starts when the app starts and looks for scheduled jobs every 1 minute
/// Jobs can only run 1 at a time so all found jobs are added to a queue and processed one after another
/// If no jobs are currently running, the worker checks every 5 seconds to see if anything has been queued
/// </remarks>
public sealed class SchedulerHostedService(IServiceScopeFactory scopes, ILogger<SchedulerHostedService> logger) : BackgroundService
{
    //run both loops on app start
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(SchedulingLoopAsync(stoppingToken), WorkerLoopAsync(stoppingToken));

    //determines what is due and enqueues it
    private async Task SchedulingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SchedulerQueue>().EnqueueDueTasksAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Scheduler could not check for due tasks"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    //processes the queue
    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<SchedulerQueueProcessor>().ProcessNextAsync(ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Scheduler worker could not process the queue"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
