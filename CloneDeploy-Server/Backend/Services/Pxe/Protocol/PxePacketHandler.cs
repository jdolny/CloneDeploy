using System.Net;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Dispatches datagrams to the original DHCP and proxy request handlers.</summary>
public static class PxePacketHandler
{
    public static PxeReplyDto? CreateReply(ReadOnlySpan<byte> data, IPEndPoint source, int port, PxeSettingsDto settings)
    {
        if (data.Length < 240 || data[2] > 16) return null;
        return CreateReply(data.ToArray(), source, port, new PxePacketConfiguration(settings));
    }

    public static PxeReplyDto? CreateReply(byte[] data, IPEndPoint source, int port, PxePacketConfiguration configuration)
    {
        // Reject incomplete headers before the legacy BinaryReader parses them.
        if (data.Length < 240 || data[2] > 16) return null;
        var request = new DHCPRequest(data, source, configuration);
        return port switch
        {
            67 => new DHCPDataReceived().Process(request),
            4011 => new ProxyDataReceived().Process(request),
            _ => null
        };
    }
}
