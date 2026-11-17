namespace CloneDeploy.SiteServices;

public sealed record SiteProxyAccess(bool ImagingEnabled = true, int ImagingLoginRequestsPerMinute = 0)
{
    public bool AgentsEnabled { get; init; } = true;
    public bool EnrollmentEnabled { get; init; } = true;
    public bool PortalEnabled { get; init; } = true;
    public bool AgentAdminEnabled { get; init; }
    public string AdminNetworks { get; init; } = "";
    public bool StorageEnabled { get; init; } = true;
    public bool MulticastEnabled { get; init; } = true;
    public void Validate()
    {
        if (ImagingLoginRequestsPerMinute is < 0 or > SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute) throw new InvalidDataException("Invalid imaging login limit.");
        ClientNetworks.Validate(AdminNetworks);
        if (AgentAdminEnabled && string.IsNullOrWhiteSpace(AdminNetworks)) throw new InvalidDataException("Administrator access requires allowed client networks.");
    }
    public bool Allows(Microsoft.AspNetCore.Http.PathString path, System.Net.IPAddress? address) => SiteProxyRoutes.RequiredCapability(path) switch
    {
        SiteProxyRoutes.Capability.AgentAdmin => AgentsEnabled && AgentAdminEnabled && ClientNetworks.Contains(AdminNetworks, address),
        SiteProxyRoutes.Capability.Portal => PortalEnabled && (!path.StartsWithSegments("/api/agent") || AgentsEnabled),
        SiteProxyRoutes.Capability.Enrollment => AgentsEnabled && EnrollmentEnabled,
        SiteProxyRoutes.Capability.Agents => AgentsEnabled,
        SiteProxyRoutes.Capability.Imaging => ImagingEnabled,
        SiteProxyRoutes.Capability.Storage => StorageEnabled,
        _ => false
    };
}
