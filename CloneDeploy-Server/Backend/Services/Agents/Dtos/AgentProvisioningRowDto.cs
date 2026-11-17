using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentProvisioningRowDto(Guid Id, string DeviceName, Guid InstallationId, string PublicKeyHash,
    string? IpAddress, DateTime RequestedAtUtc, AgentProvisioningState State)
{
    public string? HardwareUuid { get; init; }
    public string[] MacAddresses { get; init; } = [];
    public DateTime LastSeenAtUtc { get; init; }
    public string MacDisplay => string.Join(", ", MacAddresses);
}
