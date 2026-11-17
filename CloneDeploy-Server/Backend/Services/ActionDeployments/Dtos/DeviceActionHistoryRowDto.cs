using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceActionHistoryRowDto(Guid Id, string ActionName, string RequestedBy,
    DeviceTaskSource Source, DeviceTaskStatus Status, DateTime QueuedAtUtc,
    DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? Result);
