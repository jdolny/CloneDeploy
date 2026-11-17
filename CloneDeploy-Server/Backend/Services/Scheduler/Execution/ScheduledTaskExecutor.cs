using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.Extensions.Localization;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Runs a scheduled task for its selected destinations and combines the results.
/// </summary>
public sealed class ScheduledTaskExecutor(IEnumerable<IScheduledTaskHandler> handlers, ILogger<ScheduledTaskExecutor> logger, IStringLocalizer<LocalizationResources> loc) : IScheduledTaskExecutor
{
    private readonly Dictionary<ScheduledTaskType, IScheduledTaskHandler> _handlers = handlers.ToDictionary(x => x.Type);

    public bool HasHandler(ScheduledTaskType task) => _handlers.ContainsKey(task);

    public async Task<SchedulerTaskResultDto> RunAsync(ScheduledTaskType task, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(task, out var handler))
            return new SchedulerTaskResultDto(false, loc[ErrorKeys.Scheduler.HandlerNotFound, task]);

        if (handler is IScheduledBatchTaskHandler batch)
        {
            var results = await batch.RunAllAsync(ct);
            var failed = results.Count(x => x.Value is not null);
            foreach (var failure in results.Where(x => x.Value is not null))
                logger.LogError("Scheduler task {Task} failed for item {Id}: {Error}", task, failure.Key, failure.Value);
            return new SchedulerTaskResultDto(failed == 0, failed == 0
                ? loc[LocKeys.Scheduler.Completed, results.Count, results.Count]
                : loc[LocKeys.Scheduler.CompletedWithFailures, results.Count - failed, failed, results.Count]);
        }

        var ids = await handler.GetTargetsAsync(ct);

        return await RunItemsAsync(task, handler, ids, ct);
    }

    public async Task<SchedulerTaskResultDto> RunTargetAsync(ScheduledTaskType task, int targetId, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(task, out var handler))
            return new SchedulerTaskResultDto(false, loc[ErrorKeys.Scheduler.HandlerNotFound, task]);

        return await RunItemsAsync(task, handler, [targetId], ct);
    }

    private async Task<SchedulerTaskResultDto> RunItemsAsync(ScheduledTaskType task, IScheduledTaskHandler handler, List<int> ids, CancellationToken ct)
    {
        var succeeded = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await handler.RunAsync(id, ct);
                succeeded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogError(exception, "Scheduler task {Task} failed for item {Id}", task, id);
            }
        }

        return new SchedulerTaskResultDto(failed == 0, failed == 0
            ? loc[LocKeys.Scheduler.Completed, succeeded, ids.Count]
            : loc[LocKeys.Scheduler.CompletedWithFailures, succeeded, failed, ids.Count]);
    }

    public async Task<string?> GetTargetNameAsync(ScheduledTaskType task, int targetId, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(task, out var handler))
            return null;

        return await handler.GetTargetNameAsync(targetId, ct);
    }
}
