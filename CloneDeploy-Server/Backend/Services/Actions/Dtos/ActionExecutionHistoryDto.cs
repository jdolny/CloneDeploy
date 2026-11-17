using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ActionExecutionHistoryDto(Guid DeploymentId, string DeviceName, string RequestedBy,
    DateTime CreatedAtUtc, DateTime? FinishedAtUtc, DeviceTaskStatus Status, string? Result);
