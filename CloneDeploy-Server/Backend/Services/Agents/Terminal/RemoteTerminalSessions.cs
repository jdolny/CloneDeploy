using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Pairs one authenticated browser and device stream for a bounded, audited terminal session.</summary>
public sealed class RemoteTerminalSessions(IServiceScopeFactory scopes, ILogger<RemoteTerminalSessions> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly object gate = new();
    private bool disposed;
    public Session Create(int deviceId, int userId, string userName, string deviceName)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
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
            await Task.WhenAll(session.Agent.Task, session.Viewer.Task).WaitAsync(TimeSpan.FromSeconds(30), session.Stop.Token);
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
            duration.CancelAfter(TimeSpan.FromMinutes(30));
            var frames = RelayAsync(session.Agent.Task.Result, session.Viewer.Task.Result, WebSocketMessageType.Binary, AgentTerminalLimits.OutputChunkBytes, duration.Token);
            var input = RelayAsync(session.Viewer.Task.Result, session.Agent.Task.Result, WebSocketMessageType.Text, AgentTerminalLimits.MaximumMessageBytes, duration.Token);
            await Task.WhenAny(frames, input);
            await duration.CancelAsync();
            session.Agent.Task.Result.Abort(); session.Viewer.Task.Result.Abort();
            await Task.WhenAll(frames, input);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
        {
            result = ex is TimeoutException ? "Connection timed out" : "Disconnected";
        }
        catch (Exception ex) { result = "Failed"; logger.LogError(ex, $"Remote terminal relay {session.Id} failed"); }
        finally
        {
            session.Close();
            if (session.Agent.Task.IsCompletedSuccessfully) session.Agent.Task.Result.Abort();
            if (session.Viewer.Task.IsCompletedSuccessfully) session.Viewer.Task.Result.Abort();
            sessions.TryRemove(session.Id, out _);
            session.DisposeLifetime();
            session.Completed.TrySetResult();
            try
            {
                using var scope = scopes.CreateScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
                db.AuditLogs.Add(new AuditLog
                {
                    AppUserId = session.UserId, UserName = session.UserName, AuditType = AuditType.Update,
                    ObjectType = "RemoteTerminal", ObjectName = session.DeviceName, LoggedAtUtc = DateTime.UtcNow,
                    ObjectJson = JsonSerializer.Serialize(new { session.Id, session.DeviceId, Status = result, StartedAtUtc = session.StartedAtUtc })
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await db.SaveChangesAsync(timeout.Token);
            }
            catch (Exception ex) { logger.LogError(ex, $"Could not audit the end of terminal session {session.Id}"); }
        }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, WebSocketMessageType expected, int limit, CancellationToken ct)
    {
        var buffer = new byte[8192];
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
                var allowed = received.MessageType == expected;
                if (!allowed || received.MessageType != messageType || size > limit) throw new IOException("Invalid terminal stream message.");
                await destination.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, ct);
            } while (!received.EndOfMessage);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var session in sessions.Values) session.Close();
        }
    }

    /// <summary>Tracks the two single-use connection slots and ownership of a remote session.</summary>
    public sealed class Session(int deviceId, int userId, string userName, string deviceName)
    {
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
        private bool closed;
        public bool Attach(WebSocket socket, bool agent)
        {
            lock (attachGate) return !closed && (agent ? Agent : Viewer).TrySetResult(socket);
        }
        public void Close()
        {
            lock (attachGate)
            {
                if (closed) return;
                closed = true;
                Stop.Cancel();
            }
        }
        internal void DisposeLifetime() { lock (attachGate) Stop.Dispose(); }
    }
}
