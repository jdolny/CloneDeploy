using System.Net.Sockets;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalClient
{
    private const int ResponseBufferBytes = 4096;
    private const int MaximumResponseBytes = 256 * 1024;


    public static async Task<MacPortalAuthReply> AuthenticateAsync(MacPortalAuthRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacPortalAuthProtocol.SocketPath), timeout.Token);
        MacDaemonGuard.VerifyPeer(socket);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await MacPortalAuthProtocol.WriteAsync(stream, request, timeout.Token, request.Operation == "help" ? MacPortalAuthProtocol.HelpRequestBytes : MacPortalAuthProtocol.DefaultMessageBytes);
        return await MacPortalAuthProtocol.ReadAsync<MacPortalAuthReply>(stream, timeout.Token, request.Operation == "help" ? HelpAttachmentLimits.PayloadBytes : request.Operation is "notifications" or "self-service" or "self-service-icon" ? MacPortalAuthProtocol.NotificationReplyBytes : MacPortalAuthProtocol.DefaultMessageBytes);
    }
    public static async Task<MacPortalStatus> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacPortalStatus.SocketPath), timeout.Token);
        // Authenticate the daemon independently of the socket pathname.
        MacDaemonGuard.VerifyPeer(socket);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var data = new MemoryStream();
        var buffer = new byte[ResponseBufferBytes];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (data.Length + count > MaximumResponseBytes) throw new IOException(PortalText.Get("Connection.InvalidPortalResponse"));
            data.Write(buffer, 0, count);
        }
        var status = JsonSerializer.Deserialize<MacPortalStatus>(data.ToArray());
        return status is { SchemaVersion: 1 } ? status : throw new IOException(PortalText.Get("Connection.UnsupportedServiceVersion"));
    }
}
