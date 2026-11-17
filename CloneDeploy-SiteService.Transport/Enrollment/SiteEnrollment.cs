using System.Text.Json;

namespace CloneDeploy.SiteServices;

public sealed record SiteEnrollmentPackage(int FormatVersion, Guid RegistrationId, string BackendUrl,
    string CaCertificatePem, string Token, DateTime ExpiresAtUtc)
{ public string BackendAddress { get; init; } = ""; public bool IsWindowsWorker { get; init; } }
public sealed record SiteEnrollmentRequest(Guid RegistrationId, string? Token, string IdentityCsr, string HttpsCsr,
    string Hostname, string Version, string[] Addresses, long Timestamp, string Signature)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HostOs { get; init; }
    public bool IsWindowsWorker { get; init; }
    public byte[] SigningBytes() => JsonSerializer.SerializeToUtf8Bytes(this with { Signature = "" });
}
public sealed record SiteConfiguration(Guid RegistrationId, int Revision, bool ProxyEnabled, string ListenAddress,
    string PublicUrl, string BackendUrl, string IdentityCertificatePem, string HttpsCertificatePem)
{ public bool IsWindowsWorker { get; init; } public SiteProxyAccess Access { get; init; } = new(); public SiteBootSettings Boot { get; init; } = new(); public WindowsWorkerRoles WindowsWorkerRoles { get; init; } public int AuthenticationRequestsPerMinute { get; init; } public string? AgentProvisioningKeyHash { get; init; } public int PortalPort { get; init; } = ListenerPorts.DefaultPortalPort; public bool ImagingEnabled { get; init; } = true; public int? ImagingLoginRequestsPerMinute { get; init; } }
public sealed record SiteEnrollmentReply(string State, SiteConfiguration? Configuration);
public sealed record SiteStatusRequest(int AppliedRevision, bool ProxyRunning, string? Error, string Version)
{ public SiteBootStatus? Boot { get; init; } public string? HostOs { get; init; } }
