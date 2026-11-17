using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public record DashboardTaskDto(Guid DeploymentId, string Action, string Device, DeviceTaskStatus Status, DateTime? FinishedAt, string? Result);
