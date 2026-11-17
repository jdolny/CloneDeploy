using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace CloneDeploy_Agent.Contracts;

/// <summary>Protects server-to-agent frames, including SignalR handshakes and live terminal input.</summary>
public sealed class ServerSignedWebSocket(WebSocket inner,
    Func<ServerSignatures.SocketFrame, CancellationToken, Task<string>>? sign,
    Func<string, ServerSignatures.SocketFrame>? verify) : WebSocket
{
    public const int MaximumTokenBytes = 128 * 1024;
    private const int ChunkBytes = 32 * 1024;
    private long sent, received;
    private ServerSignatures.SocketFrame? pending;
    private int offset;
    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;
    public override void Abort() => inner.Abort();
    public override void Dispose() => inner.Dispose();
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseAsync(status, description, ct);
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => inner.CloseOutputAsync(status, description, ct);
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct) => SendAsync(buffer.AsMemory(), type, end, ct).AsTask();
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct = default)
    {
        if (sign is null) { await inner.SendAsync(buffer, type, end, ct); return; }
        try
        {
            do
            {
                var length = Math.Min(buffer.Length, ChunkBytes);
                var frame = new ServerSignatures.SocketFrame(sent++, (int)type, end && length == buffer.Length, buffer[..length].ToArray());
                var token = Encoding.UTF8.GetBytes(await sign(frame, ct));
                await inner.SendAsync(token.AsMemory(), WebSocketMessageType.Binary, true, ct);
                buffer = buffer[length..];
            } while (buffer.Length > 0);
        }
        catch { inner.Abort(); throw; }
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), ct);
        return new(result.Count, result.MessageType, result.EndOfMessage, CloseStatus, CloseStatusDescription);
    }
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (verify is null) return await inner.ReceiveAsync(buffer, ct);
        try
        {
            if (pending is null)
            {
                using var encoded = new MemoryStream();
                var bytes = new byte[8192];
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await inner.ReceiveAsync(bytes.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return result;
                    if (result.MessageType != WebSocketMessageType.Binary || encoded.Length + result.Count > MaximumTokenBytes)
                        throw new CryptographicException("Unsigned or oversized server frame.");
                    encoded.Write(bytes, 0, result.Count);
                } while (!result.EndOfMessage);
                pending = verify(Encoding.UTF8.GetString(encoded.GetBuffer(), 0, (int)encoded.Length));
                if (pending.Sequence != received++ || pending.Type is not (0 or 1) || pending.Data is null || pending.Data.Length > ChunkBytes)
                    throw new CryptographicException("Replayed or invalid server frame.");
                offset = 0;
            }
            var count = Math.Min(buffer.Length, pending.Data.Length - offset);
            pending.Data.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            var frameEnd = offset == pending.Data.Length;
            var receivedResult = new ValueWebSocketReceiveResult(count, (WebSocketMessageType)pending.Type, frameEnd && pending.End);
            if (frameEnd) pending = null;
            return receivedResult;
        }
        catch { inner.Abort(); throw; }
    }
}
