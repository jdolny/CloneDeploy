using System.Net;
using System.Net.Sockets;

namespace CloneDeploy.SiteService;

public static class BackendConnection
{
    public static IPAddress Parse(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            throw new InvalidDataException("Enter a specific main server IP address without a port or URL.");
        return ip;
    }

    public static void Configure(SocketsHttpHandler handler, string address)
    {
        var ip = Parse(address);
        // Override only the TCP destination. HttpClient retains the request hostname for SNI and TLS validation.
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
    }
}
