using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Holds the settings and latest run details shown for a scheduled task.
/// </summary>
public class SchedulerTaskDto
{
    public ScheduledTaskType Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Enabled { get; set; }
    public int? ScheduleId { get; set; }
    public DateTime? NextRunUtc { get; init; }
    public ScheduledTaskExecution? LastRun { get; init; }
    public List<ScheduledTaskExecution> RecentRuns { get; init; } = [];
}
