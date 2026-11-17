using System.IO.Pipes;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Agent.RemoteDesktop;

namespace CloneDeploy_Agent;

/// <summary>Bridges an authenticated remote session to an isolated desktop helper over a restricted pipe.</summary>
public sealed class AgentRemoteDesktop(AgentOptions options, AgentIdentity identity, ILogger logger) : IAsyncDisposable
{
    private const int PipeOutputBufferBytes = 65536;
    private const int PipeInputBufferBytes = 4096;
    private const int HelperExitTimeoutMilliseconds = 2000;

    private const int MaximumRequesterCharacters = 256;
    private const int MaximumRememberedRequests = 128;
    private const int IdentityPollMilliseconds = 300;

    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HelperConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? session;
    private Guid sessionId;
    public void Cancel(Guid id) { lock (gate) { if (sessionId == id) stop?.Cancel(); } }
    public AgentRemoteConsent Consent { get; } = new();
    internal Func<DesktopSessions.Session[]> SessionList { get; init; } = DesktopSessions.List;
    internal Func<DesktopSessions.Session, string> ReadSessionKey { get; init; } = AgentRemoteConsent.SessionKey;
    internal Func<Guid, int, CancellationToken, Task>? ConnectOverride { get; init; }
    private readonly Dictionary<Guid, DateTimeOffset> seenRequests = [];

    public AgentRemoteDesktopReply Start(AgentRemoteDesktopRequest request, Guid deviceGuid) => StartAsync(request, deviceGuid).GetAwaiter().GetResult();

    public Task<AgentRemoteDesktopReply> StartAsync(AgentRemoteDesktopRequest request, Guid deviceGuid)
    {
        if (request.DeviceGuid != deviceGuid || request.SessionId == Guid.Empty
            || request.ConsentProtocol != 1 || !Enum.IsDefined(request.AccessMode) || string.IsNullOrWhiteSpace(request.Requester) || request.Requester.Length > MaximumRequesterCharacters
            || request.ExpiresAtUtc < DateTimeOffset.UtcNow
            ||
            request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "Invalid or expired desktop request."));

        var selected = SessionList().FirstOrDefault(x => request.WindowsSessionId is { } id
            ? x.Id == id
            : x.Connection == "Console");

        if (selected is null || !selected.Available)
            return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "The selected Windows session is unavailable."));

        lock (gate)
        {
            foreach (var id in seenRequests.Where(x => x.Value <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) seenRequests.Remove(id);
            if (seenRequests.ContainsKey(request.SessionId) || seenRequests.Count >= MaximumRememberedRequests)
                return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "Duplicate or excessive remote request."));
            if (session is { IsCompleted: false })
                return Task.FromResult(new AgentRemoteDesktopReply(request.SessionId, false, "A remote desktop session is already active."));

            stop?.Dispose();
            stop = new CancellationTokenSource(SessionTimeout);
            sessionId = request.SessionId;
            seenRequests.Add(request.SessionId, request.ExpiresAtUtc);
            var reply = new TaskCompletionSource<AgentRemoteDesktopReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = AuthorizeAndRunAsync(request, selected, stop, reply);
            return reply.Task;
        }
    }

    private async Task AuthorizeAndRunAsync(AgentRemoteDesktopRequest request, DesktopSessions.Session selected,
        CancellationTokenSource lifetime, TaskCompletionSource<AgentRemoteDesktopReply> reply)
    {
        Task? monitor = null;
        try
        {
            var key = ReadSessionKey(selected);
            var loggedIn = SessionList().Any(x => !string.IsNullOrEmpty(x.UserName));
            var prompt = request.AccessMode == RemoteAccessMode.RequireApproval || request.AccessMode == RemoteAccessMode.ApprovalWhenLoggedIn && loggedIn;
            if (prompt && key.Length == 0) throw new InvalidOperationException("Approval is required, but no user can approve in the selected session.");
            var expiry = DateTimeOffset.UtcNow.AddSeconds(60);
            if (request.ExpiresAtUtc < expiry) expiry = request.ExpiresAtUtc;
            var decision = Consent.Begin(request.SessionId, selected.Id, key, request.Requester, expiry, () => lifetime.Cancel());
            monitor = MonitorAsync();
            if (prompt && !await decision.WaitAsync(expiry - DateTimeOffset.UtcNow, lifetime.Token))
                throw new InvalidOperationException("The user denied remote access.");
            lifetime.Token.ThrowIfCancellationRequested();
            CheckIdentity();
            Consent.Activate(request.SessionId);
            reply.TrySetResult(new(request.SessionId, true, prompt ? "User approved remote access." : "Unattended access allowed by rule."));
            await (ConnectOverride?.Invoke(request.SessionId, selected.Id, lifetime.Token) ?? RunSelectedAsync(request.SessionId, selected.Id, lifetime.Token));

            void CheckIdentity()
            {
                var sessions = SessionList();
                var now = sessions.SingleOrDefault(x => x.Id == selected.Id);
                if (now is null || !now.Available || ReadSessionKey(now) != key ||
                    prompt && !Consent.UiAvailable(request.SessionId) ||
                    request.AccessMode == RemoteAccessMode.ApprovalWhenLoggedIn && !loggedIn && sessions.Any(x => !string.IsNullOrEmpty(x.UserName)))
                    throw new InvalidOperationException("The Windows logon changed. Request access again.");
            }
            async Task MonitorAsync()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested) { CheckIdentity(); await Task.Delay(IdentityPollMilliseconds, lifetime.Token); }
                }
                catch { await lifetime.CancelAsync(); }
            }
        }
        catch (Exception ex)
        {
            var detail = ex is TimeoutException ? "User approval timed out. Access denied." :
                ex is OperationCanceledException ? "Remote access was denied, stopped, or the Windows session changed." :
                ex is InvalidOperationException ? ex.Message : "Could not verify the Windows user for remote access.";
            reply.TrySetResult(new(request.SessionId, false, detail));
            logger.LogInformation("Remote access {SessionId}: {Detail}", request.SessionId, detail);
        }
        finally
        {
            Consent.End(request.SessionId);
            await lifetime.CancelAsync();
            if (monitor is not null) await monitor;
            reply.TrySetResult(new(request.SessionId, false, "Remote access could not start."));
        }
    }

    private async Task RunSelectedAsync(Guid id, int windowsSessionId, CancellationToken ct)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var certificate = identity.GetClientCertificate()
                                    ?? throw new IOException("Agent certificate is unavailable.");
            var uri = new UriBuilder(new Uri(options.GetServerUri(), $"api/agent/v1/remote/{id}")) { Scheme = "wss" }.Uri;
            using var socket = await AgentResponseVerification.ConnectSocketAsync(uri, options, certificate, ct);
            using var sends = new SemaphoreSlim(1, 1);

            async Task SendAsync(byte[] data, WebSocketMessageType type)
            {
                await sends.WaitAsync(lifetime.Token);

                try
                {
                    await socket.SendAsync(data, type, true, lifetime.Token);
                }
                finally
                {
                    sends.Release();
                }
            }

            var pipeName = $"CloneDeploy-Desktop-{Guid.NewGuid().ToString("N")}";
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            using var current = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new PipeAccessRule(
                current.User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                PipeOutputBufferBytes,
                PipeInputBufferBytes,
                security);
            using var helper = DesktopLauncher.StartInSession(
                Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.exe"),
                pipeName,
                windowsSessionId);

            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(HelperConnectTimeout);
                await pipe.WaitForConnectionAsync(connect.Token);

                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)
                    || pid != helper.Id)
                    throw new IOException("Unexpected desktop pipe client.");

                logger.LogInformation($"Remote desktop session {id} connected");
                var frames = SendFrames();
                var input = ReceiveInput();
                var ended = await Task.WhenAny(frames, input);

                if (ended.IsFaulted)
                    logger.LogWarning(
                        $"Remote desktop session {id}: {(ended == frames ? "Desktop capture" : "Server connection")} ended: {ended.Exception?.GetBaseException().Message}");

                await lifetime.CancelAsync();
                pipe.Dispose();
                socket.Abort();

                try
                {
                    await Task.WhenAll(frames, input);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                }

                async Task SendFrames()
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var data = await DesktopPipe.ReadFrameAsync(pipe, lifetime.Token);
                        var control = data.AsSpan().StartsWith("CTL\n"u8);
                        await SendAsync(
                            control ? data[4..] : data,
                            control ? WebSocketMessageType.Text : WebSocketMessageType.Binary);
                    }
                }

                async Task ReceiveInput()
                {
                    var buffer = new byte[DesktopPipe.MaxInputBytes];

                    while (!lifetime.IsCancellationRequested)
                    {
                        var length = 0;
                        ValueWebSocketReceiveResult received;
                        do
                        {
                            if (length == buffer.Length)
                                throw new IOException("Desktop input exceeds the limit.");

                            received = await socket.ReceiveAsync(buffer.AsMemory(length), lifetime.Token);

                            if (received.MessageType == WebSocketMessageType.Close)
                                return;

                            if (received.MessageType != WebSocketMessageType.Text)
                                throw new IOException("Invalid desktop input.");

                            length += received.Count;
                        } while (!received.EndOfMessage);

                        using var command = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, length));

                        if (command.RootElement.GetProperty("type").GetString() == "sas")
                        {
                            var requestId = command.RootElement.GetProperty("requestId").GetString();

                            if (!Guid.TryParse(requestId, out _))
                                throw new InvalidDataException("Invalid desktop command ID.");

                            string detail;
                            var success = false;

                            try
                            {
                                DesktopSecureAttention.Send(windowsSessionId);
                                detail = "Ctrl+Alt+Delete sent.";
                                success = true;
                            }
                            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
                            {
                                detail = ex.Message;
                            }

                            await SendAsync(
                                DesktopPipe.ControlMessage(new
                                {
                                    type = "result",
                                    requestId,
                                    success,
                                    detail
                                })[4..],
                                WebSocketMessageType.Text);
                            continue;
                        }

                        await DesktopPipe.WriteAsync(pipe, buffer[..length], lifetime.Token);
                    }
                }
            }
            finally
            {
                pipe.Dispose();

                if (!helper.HasExited && !helper.WaitForExit(HelperExitTimeoutMilliseconds))
                    helper.Kill();
                else if (helper.HasExited && helper.ExitCode != 0)
                    logger.LogWarning($"Remote desktop helper exited with code {helper.ExitCode} for session {id}");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Remote desktop session {id} ended ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            logger.LogInformation($"Remote desktop session {id} disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        CancellationTokenSource? stopping;

        lock (gate)
        {
            stopping = stop;
            stop = null;
            stopping?.Cancel();
            pending = session;
            session = null;
        }

        if (pending is not null)
            await pending;

        stopping?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
}
