using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

public interface IScheduledTaskExecutor
{
    Task<SchedulerTaskResultDto> RunAsync(ScheduledTaskType task, CancellationToken ct);
    Task<SchedulerTaskResultDto> RunTargetAsync(ScheduledTaskType task, int targetId, CancellationToken ct);
}