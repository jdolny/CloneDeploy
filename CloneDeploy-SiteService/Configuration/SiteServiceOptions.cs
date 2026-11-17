using System.Net;

namespace CloneDeploy.SiteService;

public sealed class SiteServiceOptions
{
    public CloneDeploy.SiteServices.SiteProxyAccess Access { get; set; } = new();
    public string ListenAddress { get; set; } = "";
    public int Port { get; set; } = CloneDeploy.SiteServices.ListenerPorts.DefaultServicePort;
    public int PortalPort { get; set; } = CloneDeploy.SiteServices.ListenerPorts.DefaultPortalPort;
    public string PublicUrl { get; set; } = "";
    public string BackendAddress { get; set; } = "";
    public string BackendUrl { get; set; } = "";
    public string ServerCertificatePath { get; set; } = "";
    public string ServerCertificatePassword { get; set; } = "";
    public string? PortalCertificatePath { get; set; }
    public string? PortalCertificatePassword { get; set; }
    public string? PortalCertificateSubject { get; set; }
    public string PortalCertificateStoreLocation { get; set; } = "LocalMachine";
    public string PortalCertificateStore { get; set; } = "My";
    public string IdentityCertificatePath { get; set; } = "";
    public string IdentityCertificatePassword { get; set; } = "";
    public string BackendCaCertificatePath { get; set; } = "";
    public string AgentCaCertificatePath { get; set; } = "";
    public string? AgentProvisioningKeyHash { get; set; }
    public bool ImagingEnabled { get; set; } = true;
    public int? ImagingLoginRequestsPerMinute { get; set; }
    public int AuthenticationRequestsPerMinute { get; set; }

    public void Validate()
    {
        Access.Validate();
        if (string.IsNullOrWhiteSpace(BackendCaCertificatePath))
            throw new InvalidOperationException("Configure SiteService:BackendCaCertificatePath with the trusted CloneDeploy CA.");
        if (AuthenticationRequestsPerMinute is < 0 or > CloneDeploy.SiteServices.SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute || ImagingLoginRequestsPerMinute is < 0 or > CloneDeploy.SiteServices.SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute)
            throw new InvalidOperationException("AuthenticationRequestsPerMinute and ImagingLoginRequestsPerMinute must be between 0 and 1000000 (0 disables the limit).");
        if (string.IsNullOrWhiteSpace(AgentCaCertificatePath))
            throw new InvalidOperationException("Configure SiteService:AgentCaCertificatePath with the public CloneDeploy agent CA certificate.");
        if (!IPAddress.TryParse(ListenAddress, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            throw new InvalidOperationException("SiteService:ListenAddress must be the specific IP assigned to this service.");
        if (Port is < 1 or > CloneDeploy.SiteServices.ListenerPorts.MaximumPort || PortalPort is < 1 or > CloneDeploy.SiteServices.ListenerPorts.MaximumPort || Port == PortalPort) throw new InvalidOperationException("SiteService:Port is invalid.");
        var publicUri = ValidateUrl(PublicUrl, nameof(PublicUrl));
        var backendUri = ValidateUrl(BackendUrl, nameof(BackendUrl));
        if (publicUri.Port != Port) throw new InvalidOperationException("PublicUrl must use the listening port.");
        if (!string.IsNullOrEmpty(BackendAddress))
        {
            var backendIp = BackendConnection.Parse(BackendAddress);
            if (backendIp.Equals(ip) && backendUri.Port == Port)
                throw new InvalidOperationException("The main server IP cannot be the proxy listening IP on the same port.");
        }
        else if (publicUri == backendUri || backendUri.Port == Port && backendUri.Host.Trim('[', ']') == ip.ToString())
            throw new InvalidOperationException("BackendUrl must identify the main server, not the public proxy endpoint.");
        if (string.IsNullOrWhiteSpace(ServerCertificatePath) || string.IsNullOrWhiteSpace(IdentityCertificatePath))
            throw new InvalidOperationException("Configure the HTTPS server and proxy identity PFX files.");
    }

    private static Uri ValidateUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException($"SiteService:{name} must be an HTTPS origin without credentials, path, query or fragment.");
        return uri;
    }
}
