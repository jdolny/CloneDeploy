using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

/// <summary>Reads the local service's metadata over a verified pipe, without access to service credentials.</summary>
internal static class PortalClient
{
    private const int PipeConnectTimeoutMilliseconds = 3000;
    private const int MaximumIconBytes = 64 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;

    internal static async Task<long> HelpPresenceAsync(CancellationToken ct)
        => JsonSerializer.Deserialize<long>(await ReadResponseAsync(PortalRequests.HelpPresence, ct));

    internal static async Task<AgentWorkAccountStatus> WorkAccountAsync(AgentWorkAccountRequest input, CancellationToken ct)
        => JsonSerializer.Deserialize<AgentWorkAccountStatus>(await ReadResponseAsync(PortalRequests.WorkAccount, ct, null, JsonSerializer.SerializeToUtf8Bytes(input)))
            ?? throw new IOException(PortalText.Get("SignIn.InvalidWorkAccountResponse"));

    internal static async Task<HelpResponse> HelpAsync(HelpRequest input, CancellationToken ct)
        => JsonSerializer.Deserialize<HelpResponse>(await ReadResponseAsync(PortalRequests.Help, ct, null, JsonSerializer.SerializeToUtf8Bytes(input)))
            ?? throw new IOException(PortalText.Get("HelpDesk.InvalidResponse"));
    internal static async Task<RemoteConsentStatus> RemoteConsentAsync(RemoteConsentInput input, CancellationToken ct)
        => JsonSerializer.Deserialize<RemoteConsentStatus>(await ReadResponseAsync(PortalRequests.RemoteConsent, ct, null, JsonSerializer.SerializeToUtf8Bytes(input)))
            ?? throw new IOException(PortalText.Get("RemoteSupport.InvalidResponse"));
    internal static async Task<AgentPortalSnapshot> ReadAsync(CancellationToken ct)
        => JsonSerializer.Deserialize<AgentPortalSnapshot>(await ReadResponseAsync(PortalRequests.Snapshot, ct)) ?? throw new IOException(PortalText.Get("Connection.InvalidPortalResponse"));

    internal static async Task<AgentNotificationSnapshot> ReadNotificationSnapshotAsync(CancellationToken ct)
        => JsonSerializer.Deserialize<AgentNotificationSnapshot>(await ReadResponseAsync(PortalRequests.NotificationSnapshot, ct)) ?? throw new IOException(PortalText.Get("Notifications.InvalidSnapshot"));

    internal static async Task AcknowledgeNotificationAsync(string id, CancellationToken ct)
    {
        var accepted = JsonSerializer.Deserialize<bool>(await ReadResponseAsync(PortalRequests.AcknowledgeNotification, ct, null, JsonSerializer.SerializeToUtf8Bytes(id)));
        if (!accepted) throw new IOException(PortalText.Get("Notifications.AcknowledgeMessageFailed"));
    }

    internal static Task<byte[]> ReadIconAsync(int policyId, CancellationToken ct) => ReadResponseAsync(PortalRequests.PolicyIcon, ct, policyId);
    internal static async Task<AgentUsbStatus> UsbAsync(AgentUsbRequest input, CancellationToken ct)
        => JsonSerializer.Deserialize<AgentUsbStatus>(await ReadResponseAsync(PortalRequests.Usb, ct, null, JsonSerializer.SerializeToUtf8Bytes(input)))
            ?? throw new IOException(PortalText.Get("Usb.InvalidResponse"));
    internal static async Task<AgentImagePrepStatus> ImagePrepAsync(AgentImagePrepRequest input, CancellationToken ct) =>
        JsonSerializer.Deserialize<AgentImagePrepStatus>(await ReadResponseAsync(PortalRequests.ImagePrep, ct, null, JsonSerializer.SerializeToUtf8Bytes(input)))
        ?? throw new IOException(PortalText.Get("Imaging.InvalidPreparationResponse"));

    internal static async Task<AgentAdminStatus> AdminAsync(byte request, CancellationToken ct)
    {
        if (request is not (PortalRequests.AdminSignIn or PortalRequests.AdminStatus or PortalRequests.AdminSignOut)) throw new ArgumentOutOfRangeException(nameof(request));
        return JsonSerializer.Deserialize<AgentAdminStatus>(await ReadResponseAsync(request, ct)) ?? throw new IOException(PortalText.Get("Admin.InvalidSessionResponse"));
    }
    internal static async Task<AgentDriverPackStatus> DriverPackAsync(byte request, CancellationToken ct, AgentDriverPackRequest? input = null)
    {
        if (request is not (PortalRequests.StartDriverPack or PortalRequests.DriverPackStatus or PortalRequests.CancelDriverPack) || (request == PortalRequests.StartDriverPack && input is null)) throw new ArgumentOutOfRangeException(nameof(request));
        return JsonSerializer.Deserialize<AgentDriverPackStatus>(await ReadResponseAsync(request, ct, null,
            input is null ? null : JsonSerializer.SerializeToUtf8Bytes(input))) ?? throw new IOException(PortalText.Get("Drivers.InvalidCollectionResponse"));
    }

    private static Task<byte[]> ReadResponseAsync(byte request, CancellationToken ct) => ReadResponseAsync(request, ct, null);

    private static async Task<byte[]> ReadResponseAsync(byte request, CancellationToken ct, int? policyId, byte[]? input = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var pipe = new NamedPipeClientStream(".", "CloneDeploy-Agent-Portal-v1", PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try { await pipe.ConnectAsync(PipeConnectTimeoutMilliseconds, timeout.Token); }
        catch (TimeoutException ex) { throw new IOException(PortalText.Get("Connection.AgentNotAcceptingConnections"), ex); }
        PortalServiceIdentity.Verify(pipe.SafePipeHandle);
        await pipe.WriteAsync(new[] { request }, timeout.Token);
        if (input is not null)
        {
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, input.Length);
            await pipe.WriteAsync(prefix, timeout.Token); await pipe.WriteAsync(input, timeout.Token);
        }
        if (policyId.HasValue)
        {
            var id = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(id, policyId.Value);
            await pipe.WriteAsync(id, timeout.Token);
        }
        var header = new byte[4];
        try { await pipe.ReadExactlyAsync(header, timeout.Token); }
        catch (EndOfStreamException ex) { throw new IOException(PortalText.Get("Connection.AgentClosedWithoutResponse"), ex); }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || (length == 0 && request != PortalRequests.PolicyIcon) || length > (request == PortalRequests.PolicyIcon ? MaximumIconBytes : request == PortalRequests.Help ? HelpAttachmentLimits.PayloadBytes : MaximumResponseBytes)) throw new IOException(PortalText.Get("Connection.InvalidResponseSize"));
        var data = new byte[length]; await pipe.ReadExactlyAsync(data, timeout.Token);
        await pipe.WriteAsync(new byte[] { 0 }, timeout.Token);
        return data;
    }
}
