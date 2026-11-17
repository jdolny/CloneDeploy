using System.Net;
using System.Net.Sockets;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

public sealed record TftpSettingsDto
{
    public bool Enabled { get; set; }
    public string Interface { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 69;
    public int ResponseTimeoutMilliseconds { get; set; } = 2000;
    public int Retries { get; set; } = 5;

    public string? Validate()
    {
        if (Interface is null || !IPAddress.TryParse(Interface, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != Interface ||
            ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224)
            return ErrorKeys.Tftp.InvalidInterface;
        if (Port is < 1 or > 65535) return ErrorKeys.Tftp.InvalidPort;
        if (ResponseTimeoutMilliseconds is < 250 or > 30000 || Retries is < 0 or > 10)
            return ErrorKeys.Tftp.InvalidRetries;
        return null;
    }
}
