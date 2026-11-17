using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public record DashboardScheduledTaskDto(ScheduledTaskType Type, bool Enabled, ScheduledTaskStatus? Status, DateTime? LastRun, DateTime? LastSuccess);
