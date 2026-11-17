using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the action, devices, and timing chosen for a deployment.
/// </summary>
public sealed class DeploymentRequestDto
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid ActionGuid { get; set; }
    public DeviceSoftwareUninstallDto? SoftwareUninstall { get; set; }
    public List<WindowsUpdateIdentity> TestUpdates { get; set; } = [];
    public List<PolicyTargetSelectionDto> Targets { get; set; } = [];
    public List<int> DeviceIds { get; set; } = [];
    public DeviceTaskTiming Timing { get; set; } = DeviceTaskTiming.Now;
    public int ExpireAfterDays { get; set; } = 7;
}

public sealed record DeviceSoftwareUninstallDto(int SoftwareId, string Name, string Version, string? Arguments);
