using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

// Separate from the public read-only status socket. Explicit user-auth and help operations only.
public sealed class MacPortalAuthentication(MacConfiguration configuration, MacIdentity identity, string directory)
{
    public MacSelfService? SelfService { get; init; }
    public MacWorkIdentity? WorkIdentity { get; init; }
    public PortalNotificationJournal? Notifications { get; init; }
    public MacRemoteDesktop? Remote { get; init; }
    public MacHelpPresence? HelpPresence { get; init; }
    [DllImport("libc", SetLastError = true)] private static extern int getpeereid(int socket, out uint uid, out uint gid);
    public async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var bound = false;
        try
        {
            var parent = Path.GetDirectoryName(MacPortalAuthProtocol.SocketPath)!;
            if (!Directory.Exists(parent) || new DirectoryInfo(parent).LinkTarget is not null) throw new IOException("Portal runtime directory unavailable.");
            File.Delete(MacPortalAuthProtocol.SocketPath);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(MacPortalAuthProtocol.SocketPath)); bound = true;
            File.SetUnixFileMode(MacPortalAuthProtocol.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            listener.Listen(16);
            while (!ct.IsCancellationRequested)
            {
                using var peer = await listener.AcceptAsync(ct);
                if (getpeereid(peer.Handle.ToInt32(), out var uid, out _) != 0 || uid == 0) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var stream = new NetworkStream(peer, ownsSocket: false);
                try
                {
                    var request = await MacPortalAuthProtocol.ReadAsync<MacPortalAuthRequest>(stream, timeout.Token, MacPortalAuthProtocol.HelpRequestBytes);
                    var reply = await HandleAsync(uid, request, timeout.Token);
                    await MacPortalAuthProtocol.WriteAsync(stream, reply, timeout.Token, request.Operation == "help" ? HelpAttachmentLimits.PayloadBytes : request.Operation is "notifications" or "self-service" or "self-service-icon" ? MacPortalAuthProtocol.NotificationReplyBytes : MacPortalAuthProtocol.DefaultMessageBytes);
                }
                catch (Exception ex) when (ex is IOException or SocketException or JsonException or HttpRequestException or OperationCanceledException or ArgumentException or CryptographicException or InvalidOperationException)
                {
                    if (ct.IsCancellationRequested) break;
                    try { await MacPortalAuthProtocol.WriteAsync(stream, new MacPortalAuthReply("Unavailable"), timeout.Token); } catch (Exception) when (!ct.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Portal sign-in unavailable ({ex.GetType().Name}). Device service continues."); }
        finally { if (bound) { try { File.Delete(MacPortalAuthProtocol.SocketPath); } catch (IOException) { } } }
    }
    public async Task<MacPortalAuthReply> HandleAsync(uint uid, MacPortalAuthRequest request, CancellationToken ct)
    {
        if (uid == 0 || identity.State.DeviceGuid is null || identity.State.CertificatePem is null) return new("NotEnrolled");
        if (request.Operation is "self-service" or "self-service-run" or "self-service-status" or "self-service-icon")
            return new("SelfService", SelfService: SelfService is null ? new("Unavailable") : await SelfService.HandleAsync(uid, request, ct));
        if (request.Operation == "remote-poll") return Remote?.Poll(uid, request) ?? new("RemoteUnavailable");
        if (request.Operation is "notifications" or "notification-ack")
        {
            // The kernel peer UID, never a UI-supplied identity, owns the receipt.
            var console = WorkIdentity?.ConsoleSession() ?? MacConsoleSession.Current();
            if (console is null || console.Value.Uid != uid || Notifications is null) return new("Unavailable");
            if (request.Token is { Length: 43 }) WorkIdentity?.Watch(uid, request.Token);
            var signedIn = WorkIdentity is null ? null : await WorkIdentity.ResolveAsync(uid, request.Operation == "notification-ack", ct);
            if (request.Token is { Length: 43 } && signedIn is null) return new("SignedOut");
            var localUser = signedIn?.UserSid ?? $"macos:{uid}";
            var displayName = signedIn?.User.Name ?? console.Value.Name;
            if (request.Operation == "notification-ack")
                return new(request.NotificationId is { Length: > 0 and <= 80 } id && Notifications.Acknowledge(id, localUser, displayName)
                    ? "Acknowledged" : "Unavailable");
            return new("Notifications", Notifications: Notifications.ReadForUser(localUser, displayName));
        }
        var options = configuration.Options(directory); var server = options.GetServerUri();
        var storage = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{server.AbsoluteUri}|{identity.State.DeviceGuid}|{uid}")));
        if (request.Operation == "identity-watch" && request.Token is { Length: 43 })
        { WorkIdentity?.Watch(uid, request.Token); return new("Watching"); }
        if (request.Operation == "context") return new("Ready", StorageKey: storage);
        if (request.Operation == "help-watch" && request.Token is { Length: 43 })
            return new("Watching", Revision: HelpPresence?.Watch(uid, request.Token) ?? 0);
        if (request.Operation == "sign-out") { HelpPresence?.Remove(uid); WorkIdentity?.Remove(uid); }
        var user = $"macos:{uid}"; // OS peer identity; ignore any identity asserted in UI JSON.
        using var pool = new AgentHttpClientPool(options); using var certificate = identity.GetClientCertificate();
        using var http = pool.Create(certificate); http.Timeout = TimeSpan.FromSeconds(10); http.MaxResponseContentBufferSize = MacPortalAuthProtocol.DefaultMessageBytes;
        HttpResponseMessage response;
        if (request.Operation == "help" && request.Token is { Length: 43 } && request.Help is { } help)
        {
            http.MaxResponseContentBufferSize = HelpAttachmentLimits.PayloadBytes;
            response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/help", new AgentPortalHelpRequest(user, request.Token, help), ct);
        }
        else if (request.Operation == "start" && request.Start is { } start)
            response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/start", start with { LocalUser = user }, ct);
        else if (request.Operation == "exchange" && request.Exchange is { } exchange)
            response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/exchange", exchange with { LocalUser = user }, ct);
        else if (request.Operation is "session" or "sign-out" && request.Token is { Length: 43 })
            response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/" + request.Operation, new AgentPortalSessionRequest(user, request.Token), ct);
        else return new("InvalidRequest");
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new("SignedOut", StorageKey: storage);
            if (request.Operation == "help" && (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden))
                return new("Help", StorageKey: storage, Help: await response.Content.ReadFromJsonAsync<HelpResponse>(ct) ?? throw new IOException("Empty help response."));
            if (!response.IsSuccessStatusCode) return new("Unavailable", StorageKey: storage);
            if (request.Operation == "sign-out") return new("SignedOut", StorageKey: storage);
            if (request.Operation == "start")
            {
                var result = await response.Content.ReadFromJsonAsync<AgentPortalSignInResponse>(ct) ?? throw new IOException("Empty sign-in response.");
                if (result.BrowserPort is < 1 or > 65535 || result.SignInPath != $"/agent/portal-sign-in/{result.RequestId:D}") throw new IOException("Invalid sign-in address.");
                return new("Pending", storage, new Uri(new UriBuilder(server) { Port = result.BrowserPort }.Uri, result.SignInPath.TrimStart('/')).AbsoluteUri, Start: result);
            }
            var session = await response.Content.ReadFromJsonAsync<AgentPortalUserSession>(ct) ?? throw new IOException("Empty session response.");
            WorkIdentity?.Watch(uid, session.Token);
            return new("SignedIn", storage, Session: session);
        }
    }
}
