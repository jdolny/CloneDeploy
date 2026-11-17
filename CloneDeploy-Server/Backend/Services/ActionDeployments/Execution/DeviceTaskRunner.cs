using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks waiting tasks in the background and notifies devices when work is ready.
/// </summary>
public sealed class DeviceTaskRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeviceTaskRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Device task queue maintenance failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<DeviceTaskQueue>();
        await queue.MaintainAsync(ct);
        var notifier = scope.ServiceProvider.GetService<IDeviceTaskNotifier>();
        if (notifier is null) return; // Agent transport has not been installed yet. Tasks remain queued.
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        var retryBefore = now.AddSeconds(-30);
        List<Guid> devices;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await DeviceTaskQueue.LockAsync(db, ct);
            var localServer = (notifier as AgentDeviceTaskNotifier)?.ServerInstanceId;
            var tasks = await db.DeviceTasks.Where(x => x.Status == DeviceTaskStatus.Queued && x.Deployment.Timing == DeviceTaskTiming.Now &&
                                                        x.Deployment.ExpiresAtUtc > now && (localServer == null || db.SocketConnections.Any(c => c.DeviceId == x.DeviceId && c.ServerInstanceId == localServer)) && (x.LastNotifiedAtUtc == null || x.LastNotifiedAtUtc < retryBefore))
                .OrderBy(x => x.Deployment.CreatedAtUtc).Take(100).ToListAsync(ct);
            foreach (var task in tasks) task.LastNotifiedAtUtc = now;
            devices = tasks.Select(x => x.DeviceGuid).Distinct().ToList();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var device in devices)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await notifier.NotifyAsync(device, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not notify device {DeviceGuid}; its tasks remain queued", device);
            }
        }
    }
}