using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Agent.RemoteDesktop;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Relays an authorized console session; user capture never receives device credentials.</summary>
public sealed class MacRemoteDesktop(MacConfiguration configuration, MacIdentity identity, string directory) : IAsyncDisposable
{
    private const int SocketDirectoryPollMilliseconds = 200;

    private const int MaximumRequesterCharacters = 256;
    private const int MaximumRememberedRequests = 128;
    private const int IdentityPollMilliseconds = 300;

    private readonly object gate = new();
    private readonly Dictionary<uint, Lease> leases = [];
    private readonly Dictionary<Guid, DateTimeOffset> seen = [];
    private Pending? pending;
    private bool disposed;
    internal Func<(uint Uid, string Name)?> ConsoleUser { get; init; } = MacConsoleSession.Current;
    private sealed record Lease(Guid Client, DateTimeOffset Seen, bool Capture, bool Control);
    private sealed class Pending(AgentRemoteDesktopRequest request, uint uid, Guid client)
    {
        public AgentRemoteDesktopRequest Request = request;
        public uint Uid = uid;
        public Guid Client = client;
        public CancellationTokenSource Stop = new(TimeSpan.FromMinutes(30));
        public TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Stream> Stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Claimed, Active;
        public Task? Work;
    }

    public MacPortalAuthReply Poll(uint uid, MacPortalAuthRequest input)
    {
        lock (gate)
        {
            if (disposed || uid == 0 || input.RemoteClient == Guid.Empty) return new("Unavailable");
            if (ConsoleUser()?.Uid != uid) return new("RemoteUnavailable");
            leases[uid] = new(input.RemoteClient, DateTimeOffset.UtcNow, input.CanCapture, input.CanControl);
            foreach (var stale in leases.Where(x => DateTimeOffset.UtcNow - x.Value.Seen > TimeSpan.FromSeconds(10)).Select(x => x.Key).ToArray()) leases.Remove(stale);
            var item = pending;
            if (item is null || item.Uid != uid || item.Client != input.RemoteClient || item.Stop.IsCancellationRequested) return new("RemoteIdle");
            if (input.RemoteConsent is { } consent && consent.SessionId == item.Request.SessionId)
            {
                if (consent.Action == RemoteConsentAction.Allow) item.Decision.TrySetResult(input.CanCapture);
                else if (consent.Action == RemoteConsentAction.Deny) { item.Decision.TrySetResult(false); item.Stop.Cancel(); }
                else if (consent.Action == RemoteConsentAction.Stop) item.Stop.Cancel();
            }
            return new(item.Active ? "RemoteActive" : "RemotePending", RemoteRequest: item.Request);
        }
    }

    public AgentDesktopSession[] List(AgentDesktopSessionsRequest request, Guid device)
    {
        if (request.DeviceGuid != device || request.ExpiresAtUtc < DateTimeOffset.UtcNow || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return [];
        lock (gate)
        {
            if (disposed || ConsoleUser() is not { } user) return [];
            var ready = leases.TryGetValue(user.Uid, out var lease) && DateTimeOffset.UtcNow - lease.Seen < TimeSpan.FromSeconds(10);
            return [new((int)user.Uid, user.Name, "Console", !ready ? "Open CloneDeploy Portal" : !lease!.Capture ?
                "Screen Recording permission required" : lease.Control ? "Ready" : "View only — Accessibility permission required",
                ready && lease!.Capture) { ConsentProtocol = 1 }];
        }
    }

    public Task<AgentRemoteDesktopReply> StartAsync(AgentRemoteDesktopRequest request, Guid device)
    {
        lock (gate)
        {
            if (disposed || request.DeviceGuid != device || request.SessionId == Guid.Empty || request.ConsentProtocol != 1 ||
                !Enum.IsDefined(request.AccessMode) || string.IsNullOrWhiteSpace(request.Requester) || request.Requester.Length > MaximumRequesterCharacters ||
                request.ExpiresAtUtc <= DateTimeOffset.UtcNow || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
                return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "Invalid or expired remote request."));
            var user = ConsoleUser();
            if (user is null || request.WindowsSessionId != (int)user.Value.Uid ||
                !leases.TryGetValue(user.Value.Uid, out var lease) || !lease.Capture || DateTimeOffset.UtcNow - lease.Seen > TimeSpan.FromSeconds(10))
                return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "Open the Mac portal and grant Screen Recording permission in the active console session."));
            foreach (var id in seen.Where(x => x.Value <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) seen.Remove(id);
            if (pending is not null || seen.ContainsKey(request.SessionId) || seen.Count >= MaximumRememberedRequests)
                return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "A remote session is active, or this request was already used."));
            seen[request.SessionId] = request.ExpiresAtUtc;
            var item = pending = new Pending(request, user.Value.Uid, lease.Client);
            // The portal always acknowledges readiness, even for unattended rules.
            var reply = new TaskCompletionSource<AgentRemoteDesktopReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            item.Work = RunAsync(item, reply);
            return reply.Task;
        }
    }

    public void Cancel(Guid id) { lock (gate) { if (pending?.Request.SessionId == id) pending.Stop.Cancel(); } }
    public async Task StopAsync()
    {
        Task? work;
        lock (gate) { pending?.Stop.Cancel(); work = pending?.Work; }
        if (work is not null) await work;
    }

    private async Task RunAsync(Pending item, TaskCompletionSource<AgentRemoteDesktopReply> reply)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(item.Stop.Token);
        var ct = lifetime.Token;
        var monitor = Monitor();
        try
        {
            var wait = item.Request.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
            if (wait <= TimeSpan.Zero || !await item.Decision.Task.WaitAsync(wait, ct)) throw new IOException("The Mac user denied access.");
            using var certificate = identity.GetClientCertificate() ?? throw new IOException("Agent certificate unavailable.");
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            using var socket = await AgentResponseVerification.ConnectSocketAsync(new UriBuilder(new Uri(configuration.Options(directory).GetServerUri(), $"api/agent/v1/remote/{item.Request.SessionId}")) { Scheme = "wss" }.Uri, configuration.Options(directory), certificate, connect.Token);
            using var stream = await item.Stream.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            lock (gate) item.Active = true;
            reply.TrySetResult(new(item.Request.SessionId, true, "Mac console remote access started."));
            var frames = Frames();
            var input = Input();
            try { await await Task.WhenAny(frames, input); }
            finally
            {
                lifetime.Cancel(); socket.Abort(); stream.Dispose();
                try { await Task.WhenAll(frames, input); } catch (Exception) when (ct.IsCancellationRequested) { }
            }
            async Task Frames()
            {
                while (!ct.IsCancellationRequested)
                {
                    var data = await DesktopPipe.ReadFrameAsync(stream, ct);
                    var control = data.AsSpan().StartsWith("CTL\n"u8);
                    await socket.SendAsync(control ? data.AsMemory(4) : data, control ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
                }
            }
            async Task Input()
            {
                var buffer = new byte[DesktopPipe.MaxInputBytes];
                while (!ct.IsCancellationRequested)
                {
                    var length = 0;
                    ValueWebSocketReceiveResult part;
                    do
                    {
                        if (length == buffer.Length) throw new IOException("Remote input exceeds limit.");
                        part = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                        if (part.MessageType == WebSocketMessageType.Close) return;
                        if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Invalid remote input.");
                        length += part.Count;
                    } while (!part.EndOfMessage);
                    await DesktopPipe.WriteAsync(stream, buffer[..length], ct);
                }
            }
        }
        catch (Exception ex)
        {
            var detail = ex is TimeoutException ? "Mac approval or desktop connection timed out." :
                ex is OperationCanceledException ? "Remote access ended or the Mac console changed." : ex.Message;
            reply.TrySetResult(new(item.Request.SessionId, false, detail));
            Console.WriteLine($"Remote viewing {item.Request.SessionId}: {detail}");
        }
        finally
        {
            lifetime.Cancel(); await monitor;
            lock (gate) { if (pending == item) pending = null; }
            item.Ended.TrySetResult(true); item.Stop.Dispose();
        }

        async Task Monitor()
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        if (ConsoleUser()?.Uid != item.Uid || !leases.TryGetValue(item.Uid, out var lease) ||
                            lease.Client != item.Client || !lease.Capture || DateTimeOffset.UtcNow - lease.Seen > TimeSpan.FromSeconds(10))
                            lifetime.Cancel();
                    }
                    await Task.Delay(IdentityPollMilliseconds, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch { lifetime.Cancel(); }
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int getpeereid(int socket, out uint uid, out uint gid);
    public async Task RunIpcAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = MacRemoteProtocol.SocketPath;
        var parent = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(parent) && !ct.IsCancellationRequested) await Task.Delay(SocketDirectoryPollMilliseconds, ct);
        if (new DirectoryInfo(parent).LinkTarget is not null) throw new IOException("Invalid remote socket directory.");
        File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(4);
        using var slots = new SemaphoreSlim(4);
        var workers = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var peer = await listener.AcceptAsync(ct);
                if (!slots.Wait(0)) { peer.Dispose(); continue; }
                workers.RemoveAll(x => x.IsCompleted);
                workers.Add(Handle(peer));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await StopAsync(); await Task.WhenAll(workers); File.Delete(path); }

        async Task Handle(Socket peer)
        {
            try
            {
                using (peer)
                using (var stream = new NetworkStream(peer))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    if (getpeereid(peer.Handle.ToInt32(), out var uid, out _) != 0 || uid == 0) return;
                    var input = await MacPortalAuthProtocol.ReadAsync<MacRemoteAttach>(stream, timeout.Token);
                    await AttachAsync(uid, input, stream, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Text.Json.JsonException or OperationCanceledException) { }
            finally { peer.Dispose(); slots.Release(); }
        }
    }

    internal async Task AttachAsync(uint uid, MacRemoteAttach input, Stream stream, CancellationToken ct)
    {
        Pending item;
        lock (gate)
        {
            var current = pending;
            if (current is null || current.Uid != uid || current.Client != input.Client || current.Request.SessionId != input.Session ||
                current.Claimed || current.Stop.IsCancellationRequested || !current.Decision.Task.IsCompletedSuccessfully || !current.Decision.Task.Result)
                throw new IOException("Remote attachment rejected.");
            item = current; item.Claimed = true;
        }
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(3));
            await MacPortalAuthProtocol.WriteAsync(stream, new MacPortalAuthReply("RemoteAttached"), handshake.Token);
            item.Stream.TrySetResult(stream);
            await item.Ended.Task.WaitAsync(ct);
        }
        finally { Cancel(item.Request.SessionId); }
    }

    public async ValueTask DisposeAsync() { lock (gate) disposed = true; await StopAsync(); }
}
