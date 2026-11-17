using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentRowDto(Guid Id, string ActionName, string RequestedBy, DateTime CreatedAtUtc, DeviceTaskTiming Timing, int Total, int Queued, int Running, int Succeeded, int Failed)
{
    public int Cancelled { get; init; }
    public int Unknown { get; init; }
    public DeviceTaskSource Source { get; init; }
}