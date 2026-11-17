using System.Buffers.Binary;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

public sealed record MacPortalAuthRequest(string Operation, AgentPortalSignInRequest? Start = null, AgentPortalCodeRequest? Exchange = null, string? Token = null, HelpRequest? Help = null, Guid RemoteClient = default, bool CanCapture = false, bool CanControl = false, RemoteConsentInput? RemoteConsent = null, string? NotificationId = null, int PolicyId = 0, Guid RequestId = default);
public sealed record MacPortalAuthReply(string Status, string? StorageKey = null, string? SignInUrl = null,
    AgentPortalSignInResponse? Start = null, AgentPortalUserSession? Session = null, HelpResponse? Help = null, long Revision = 0, AgentRemoteDesktopRequest? RemoteRequest = null, AgentPortalNotification[]? Notifications = null, MacSelfServiceReply? SelfService = null);
public sealed record MacSelfServiceReply(string Status, string? Message = null, AgentPortalPolicy[]? Policies = null, Guid RequestId = default, int? Result = null, byte[]? Icon = null);
public static class MacPortalAuthProtocol
{
    public const int DefaultMessageBytes = 16384;

    public const string SocketPath = "/var/run/clonedeploy-portal/auth.sock";
    public const int NotificationReplyBytes = 1024 * 1024;
    public const int HelpRequestBytes = HelpAttachmentLimits.PayloadBytes;
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct, int maxBytes = DefaultMessageBytes)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > maxBytes) throw new IOException("Invalid portal authentication message.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("Empty portal authentication message.");
    }
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct, int maxBytes = DefaultMessageBytes)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > maxBytes) throw new IOException("Oversized portal authentication message.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(data, ct);
    }
}
