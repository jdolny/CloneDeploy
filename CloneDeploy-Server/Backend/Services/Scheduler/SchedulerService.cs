using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

// Single UI entry point for scheduler settings and manual runs.
/// <summary>
/// Lets users manage background task schedules and request immediate runs.
/// </summary>
public sealed class SchedulerService(SchedulerAdministration tasks, SchedulerQueue queue, ILogger<SchedulerService> logger)
{
    public Task<ServiceResult<List<SchedulerTaskDto>>> GetScheduledTaskViews(CancellationToken ct = default) =>
        ExecuteAsync(() => tasks.GetScheduledTaskViews(ct));

    public Task<ServiceResult<ScheduledTask>> SaveAsync(ScheduledTaskType id, int? scheduleId, bool enabled, CancellationToken ct = default) =>
        ExecuteAsync(() => tasks.SaveAsync(id, scheduleId, enabled, ct));

    public Task<ServiceResult<long>> RunNowAsync(ScheduledTaskType id, CancellationToken ct = default) =>
        ExecuteAsync(() => queue.EnqueueNowAsync(id, ct));

    public Task<ServiceResult<long>> RunTargetNowAsync(ScheduledTaskType id, int targetId, CancellationToken ct = default) =>
        ExecuteAsync(() => queue.EnqueueTargetNowAsync(id, targetId, ct));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Scheduler.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Scheduler.Failed", null, operationName);
}
