using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Adds due or manually requested tasks to the queue while avoiding duplicate waiting runs.
/// </summary>
public sealed class SchedulerQueue(ICloneDeployDbFactory dbFactory, TimeProvider clock, ScheduleCalculator scheduleService, SchedulerGate gate, ScheduledTaskExecutor executor)
{
    public Task<ServiceResult<long>> EnqueueNowAsync(ScheduledTaskType id, CancellationToken ct) => ManualEnqueueAsync(id, null, ct);

    public Task<ServiceResult<long>> EnqueueTargetNowAsync(ScheduledTaskType id, int targetId, CancellationToken ct) => ManualEnqueueAsync(id, targetId, ct);

    private async Task<ServiceResult<long>> ManualEnqueueAsync(ScheduledTaskType id, int? targetId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateContextAsync();
            if (!await db.ScheduledTasks.AnyAsync(x => x.Id == id, ct))
                return ServiceResult<long>.Fail(ErrorKeys.Common.NotFound, id);
            if (!executor.HasHandler(id))
                return ServiceResult<long>.Fail(ErrorKeys.Scheduler.HandlerNotFound, id);
            string? targetName = null;

            if (targetId.HasValue)
            {
                targetName = await executor.GetTargetNameAsync(id, targetId.Value, ct);

                if (targetName is null)
                    return ServiceResult<long>.Fail(ErrorKeys.Common.NotFound, targetId.Value);
            }

            var pendingTask = await db.ScheduledTaskExecutions.Where(x => x.TaskId == id && x.TargetId == targetId && (x.Status == ScheduledTaskStatus.Queued || (targetId == null && x.Status == ScheduledTaskStatus.Running)))
                .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);

            if (pendingTask is not null) return ServiceResult<long>.Fail(ErrorKeys.Scheduler.AlreadyQueued, pendingTask.Id);

            var queuedRunTask = new ScheduledTaskExecution
            {
                TaskId = id,
                TargetId = targetId,
                TargetName = targetName,
                QueuedAtUtc = clock.GetUtcNow().UtcDateTime,
                Status = ScheduledTaskStatus.Queued
            };

            db.Add(queuedRunTask);
            await db.SaveChangesAsync(ct);
            return ServiceResult<long>.Ok(queuedRunTask.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EnqueueDueTasksAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateContextAsync();
            var now = clock.GetUtcNow().UtcDateTime;
            var allEnabledTasks = await db.ScheduledTasks.Include(x => x.Schedule).Where(x => x.IsEnabled).OrderBy(x => x.Id).ToListAsync(ct);
            foreach (var task in allEnabledTasks)
            {
                if (task.Schedule is null) continue;
                if (task.LastEvaluatedAtUtc is null)
                {
                    task.LastEvaluatedAtUtc = now;
                    continue;
                }

                if (task.LastEvaluatedAtUtc > now) continue;

                var nextScheduled = scheduleService.GetNextScheduledTime(task.Schedule, task.LastEvaluatedAtUtc.Value);

                if (nextScheduled <= now)
                {
                    var alreadyQueued = await db.ScheduledTaskExecutions.AnyAsync(x => x.TaskId == task.Id &&
                                                                                       (x.Status == ScheduledTaskStatus.Queued || x.Status == ScheduledTaskStatus.Running), ct);

                    if (!alreadyQueued)
                    {
                        db.Add(new ScheduledTaskExecution
                        {
                            TaskId = task.Id,
                            ScheduledForUtc = nextScheduled,
                            QueuedAtUtc = now,
                            Status = ScheduledTaskStatus.Queued
                        });
                    }
                }

                task.LastEvaluatedAtUtc = now;
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }
}