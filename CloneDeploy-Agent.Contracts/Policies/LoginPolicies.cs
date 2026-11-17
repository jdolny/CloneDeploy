using System.Buffers.Binary;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

/// <summary>One user-context action selected by the service; attached files follow the JSON frame.</summary>
public sealed record LoginPolicyCommand(string Kind, AgentPolicyAction? Action = null, string? Detail = null);
public sealed record LoginPolicyResult(int ExitCode, string Output, string Error);

/// <summary>Bounds JSON messages exchanged over the private login execution pipe.</summary>
public static class LoginPolicyWire
{
    private const int MaximumMessageBytes = 16 * 1024 * 1024;

    public const string PipeName = "CloneDeploy-Agent-Login-v1";
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Login message exceeds its size limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(payload, ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid login message size.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, ct);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty login message.");
    }
}
