using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Pairs one authenticated browser and device stream for a bounded, audited desktop session.</summary>
public sealed class RemoteDesktopSessions(IServiceScopeFactory scopes, ILogger<RemoteDesktopSessions> logger)
{
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly object gate = new();
    public Session Create(int deviceId, int userId, string userName, string deviceName)
    {
        lock (gate)
        {
            if (sessions.Count >= 100 || sessions.Values.Any(x => x.DeviceId == deviceId))
                throw new InvalidOperationException("A remote session is already active for this device, or the server session limit has been reached.");
            var session = new Session(deviceId, userId, userName, deviceName);
            sessions[session.Id] = session;
            _ = RunAsync(session);
            return session;
        }
    }
    public Session? Find(Guid id) => sessions.GetValueOrDefault(id);

    private async Task RunAsync(Session session)
    {
        string result = "Disconnected";
        try
        {
            await Task.WhenAll(session.Agent.Task, session.Viewer.Task).WaitAsync(TimeSpan.FromSeconds(100), session.Stop.Token);
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
            duration.CancelAfter(TimeSpan.FromMinutes(30));
            var frames = RelayAsync(session.Agent.Task.Result, session.Viewer.Task.Result, WebSocketMessageType.Binary, 4 * 1024 * 1024, duration.Token);
            var input = RelayViewerAsync(session, duration.Token);
            await Task.WhenAny(frames, input);
            await duration.CancelAsync();
            session.Agent.Task.Result.Abort(); session.Viewer.Task.Result.Abort();
            await Task.WhenAll(frames, input);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            result = ex is TimeoutException ? "Connection timed out" : "Disconnected";
        }
        catch (Exception ex) { result = "Failed"; logger.LogError(ex, "Remote desktop relay {SessionId} failed", session.Id); }
        finally
        {
            session.Close();
            if (session.Agent.Task.IsCompletedSuccessfully) session.Agent.Task.Result.Abort();
            if (session.Viewer.Task.IsCompletedSuccessfully) session.Viewer.Task.Result.Abort();
            sessions.TryRemove(session.Id, out _);
            session.Completed.TrySetResult();
            try
            {
                using var scope = scopes.CreateScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
                db.AuditLogs.Add(new AuditLog
                {
                    AppUserId = session.UserId, UserName = session.UserName, AuditType = AuditType.Update,
                    ObjectType = "RemoteDesktop", ObjectId = session.DeviceId, ObjectName = session.DeviceName, LoggedAtUtc = DateTime.UtcNow,
                    ObjectJson = JsonSerializer.Serialize(new { session.Id, session.DeviceId, Status = result, StartedAtUtc = session.StartedAtUtc })
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await db.SaveChangesAsync(timeout.Token);
            }
            catch (Exception ex) { logger.LogError(ex, "Could not audit the end of desktop session {SessionId}", session.Id); }
        }
    }

    // Buffer complete messages before forwarding: fragmented input must never bypass the view-only boundary.
    private static async Task RelayViewerAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var size = 0;
            ValueWebSocketReceiveResult received;
            do
            {
                if (size == buffer.Length) throw new IOException("Desktop input is too large.");
                received = await session.Viewer.Task.Result.ReceiveAsync(buffer.AsMemory(size), ct);
                if (received.MessageType == WebSocketMessageType.Close) return;
                if (received.MessageType != WebSocketMessageType.Text) throw new IOException("Invalid desktop input.");
                size += received.Count;
            } while (!received.EndOfMessage);
            if (!session.CanControl && !IsViewOnlyMessage(buffer.AsMemory(0, size))) continue;
            await session.Agent.Task.Result.SendAsync(buffer.AsMemory(0, size), WebSocketMessageType.Text, true, ct);
        }
    }

    public static bool IsViewOnlyMessage(ReadOnlyMemory<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            // Clipboard access and all keyboard/mouse/SAS commands require control permission.
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() is "quality" or "frameRate" or "monitor" or "release";
        }
        catch (JsonException) { return false; }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, WebSocketMessageType expected, int limit, CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            var size = 0;
            WebSocketMessageType? messageType = null;
            ValueWebSocketReceiveResult received;
            do
            {
                received = await source.ReceiveAsync(buffer.AsMemory(), ct);
                if (received.MessageType == WebSocketMessageType.Close) return;
                size += received.Count;
                messageType ??= received.MessageType;
                var allowed = received.MessageType == expected || expected == WebSocketMessageType.Binary && received.MessageType == WebSocketMessageType.Text;
                var messageLimit = received.MessageType == WebSocketMessageType.Text ? Math.Min(limit, 128 * 1024) : limit;
                if (!allowed || received.MessageType != messageType || size > messageLimit) throw new IOException("Invalid desktop stream message.");
                await destination.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, ct);
            } while (!received.EndOfMessage);
        }
    }

    /// <summary>Tracks the two single-use connection slots and ownership of a remote session.</summary>
    public sealed class Session(int deviceId, int userId, string userName, string deviceName)
    {
        public bool CanControl { get; internal set; }
        public Guid Id { get; } = Guid.NewGuid();
        public int DeviceId { get; } = deviceId;
        public int UserId { get; } = userId;
        public string UserName { get; } = userName;
        public string DeviceName { get; } = deviceName;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource<WebSocket> Agent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WebSocket> Viewer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object attachGate = new();
        public bool Attach(WebSocket socket, bool agent)
        {
            lock (attachGate) return !Stop.IsCancellationRequested && (agent ? Agent : Viewer).TrySetResult(socket);
        }
        public void Close() { lock (attachGate) Stop.Cancel(); }
    }
}
