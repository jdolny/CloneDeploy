using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

// Deliberately contains no credentials, URLs, directory identity or privileged commands.
public sealed record MacPortalStatus
{
    public const string SocketPath = "/var/run/clonedeploy-portal/status.sock";
    public int SchemaVersion { get; init; } = 1;
    public string DeviceName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Enrollment { get; init; } = "Starting";
    public bool Connected { get; init; }
    public DateTimeOffset? LastCheckinUtc { get; init; }
    public AgentBranding Branding { get; init; } = new();
    public AgentFeatures Features { get; init; } = new();
}
