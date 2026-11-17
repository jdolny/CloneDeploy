using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed class MacTerminal(AgentOptions options, MacIdentity identity) : IAsyncDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? session;
    private bool disposed;

    public AgentTerminalReply Start(AgentTerminalRequest request, Guid deviceGuid)
    {
        if (deviceGuid == Guid.Empty || request.DeviceGuid != deviceGuid || request.SessionId == Guid.Empty || request.Shell != AgentTerminalShell.Zsh
            || request.ExpiresAtUtc < DateTimeOffset.UtcNow || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            return new(request.SessionId, false, "Invalid or expired terminal request.");
        if (!OperatingSystem.IsMacOS())
            return new(request.SessionId, false, "This terminal requires macOS.");

        lock (gate)
        {
            if (disposed || session is { IsCompleted: false })
                return new(request.SessionId, false, "The agent is busy. Close the existing terminal or wait for the current operation to finish.");
            stop?.Dispose();
            stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var token = stop.Token;
            session = Task.Run(() => RunAsync(request, token));
        }
        return new(request.SessionId, true, "Starting zsh terminal as root.");
    }

    private async Task RunAsync(AgentTerminalRequest request, CancellationToken ct)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var idle = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var idleRegistration = idle.Token.Register(() => lifetime.Cancel());
            using var certificate = identity.GetClientCertificate() ?? throw new IOException("Agent certificate is unavailable.");
            var uri = new UriBuilder(new Uri(options.GetServerUri(), $"api/agent/v1/terminal/{request.SessionId}")) { Scheme = "wss" }.Uri;
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(20));
            using var socket = await AgentResponseVerification.ConnectSocketAsync(uri, options, certificate, connect.Token);

            using var console = new MacPseudoTerminal();
            using var credits = new SemaphoreSlim(AgentTerminalLimits.OutputWindow, AgentTerminalLimits.OutputWindow);
            var commands = Channel.CreateBounded<(byte[]? Data, int Columns, int Rows)>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            var outstanding = 0;
            var output = Task.Run(async () =>
            {
                var buffer = new byte[AgentTerminalLimits.OutputChunkBytes];
                while (true)
                {
                    int length;
                    try { length = await console.ReadAsync(buffer, lifetime.Token); }
                    catch (IOException) { return; }
                    if (length == 0) return;
                    if (lifetime.IsCancellationRequested) return;
                    try
                    {
                        await credits.WaitAsync(lifetime.Token);
                        Interlocked.Increment(ref outstanding);
                        await socket.SendAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, true, lifetime.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                    {
                        lifetime.Cancel();
                    }
                }
            });
            // Keep pipe writes separate from receipt of output acknowledgments. A blocked stdin
            // must not prevent the browser from allowing stdout to continue draining.
            var writer = Task.Run(async () =>
            {
                await foreach (var command in commands.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (command.Data is { } data)
                    {
                        await console.WriteAsync(data, lifetime.Token);
                    }
                    else console.Resize(command.Columns, command.Rows);
                }
            });
            var input = Task.Run(async () =>
            {
                var buffer = new byte[AgentTerminalLimits.MaximumMessageBytes];
                while (!lifetime.IsCancellationRequested)
                {
                    var length = 0;
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        if (length == buffer.Length) throw new IOException("Terminal input exceeded its limit.");
                        received = await socket.ReceiveAsync(buffer.AsMemory(length), lifetime.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        if (received.MessageType != WebSocketMessageType.Text) throw new IOException("Invalid terminal input.");
                        length += received.Count;
                    } while (!received.EndOfMessage);

                    using var message = JsonDocument.Parse(buffer.AsMemory(0, length));
                    var root = message.RootElement;
                    switch (root.GetProperty("type").GetString())
                    {
                        case "ack":
                            if (Interlocked.Decrement(ref outstanding) < 0) throw new IOException("Unexpected terminal acknowledgment.");
                            credits.Release();
                            break;
                        case "input":
                            var data = root.GetProperty("data").GetString() ?? "";
                            if (data.Length > AgentTerminalLimits.MaximumInputBytes) throw new IOException("Terminal input exceeded its limit.");
                            if (data.Length > 0) idle.CancelAfter(TimeSpan.FromMinutes(10));
                            if (!commands.Writer.TryWrite((Encoding.UTF8.GetBytes(data), 0, 0)))
                                throw new IOException("Terminal input queue is full.");
                            break;
                        case "resize":
                            var columns = root.GetProperty("columns").GetInt32();
                            var rows = root.GetProperty("rows").GetInt32();
                            if (!AgentTerminalLimits.ValidSize(columns, rows) || !commands.Writer.TryWrite((null, columns, rows)))
                                throw new IOException("Invalid terminal resize.");
                            break;
                        default:
                            throw new IOException("Unknown terminal message.");
                    }
                }
            });
            var exited = console.WaitForExitAsync(lifetime.Token);
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} Terminal session {request.SessionId} connected as root.");
            try
            {
                await Task.WhenAny(input, output, writer, exited, Task.Delay(Timeout.Infinite, lifetime.Token));
            }
            finally
            {
                await lifetime.CancelAsync();
                socket.Abort();
                console.Close();
                try { await Task.WhenAll(input, output, writer, exited); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException
                    or ObjectDisposedException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} Terminal session {request.SessionId} ended: {ex.GetType().Name}.");
        }
        finally
        {
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} Terminal session {request.SessionId} disconnected.");
        }
    }

    public async Task StopAsync()
    {
        Task? pending;
        CancellationTokenSource? stopping;
        lock (gate)
        {
            pending = session;
            stopping = stop;
            stopping?.Cancel();
        }
        if (pending is not null) await pending;
        lock (gate)
        {
            if (stop == stopping)
            {
                stop?.Dispose();
                stop = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await StopAsync();
    }
}
