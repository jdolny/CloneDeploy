using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public record DeviceSearchResultDto
{
    public Dictionary<int, string> CustomAttributeValues { get; init; } = [];
    public DateTime? ArchivedAtUtc { get; init; }
    public int Id { get; init; }
    public string? Name { get; init; }
    public DateTime? LastCheckinTime { get; init; }
    public string? LastIp { get; init; }
    public string? ClientVersion { get; init; }
    public string AgentPlatform { get; init; } = CloneDeploy_Agent.Contracts.AgentPlatform.Windows;
    public string? LastLoggedInUser { get; init; }
    public DateTime? ProvisionedTime { get; init; }
    public string Status { get; init; } = null!;
    public string Activity { get; init; } = "Activity unavailable";
    public string? Manufacturer { get; init; }
    public string? Description { get; init; }
    public string? Model { get; init; }
    public string? Domain { get; init; }
    public string? OsName { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }
    public DeviceProvisionStatus ProvisionStatus { get; init; }
    public bool IsAdSync { get; init; }
    public bool AdDisabled { get; init; }
}
