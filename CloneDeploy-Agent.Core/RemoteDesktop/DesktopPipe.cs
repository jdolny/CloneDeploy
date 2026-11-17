using System.Buffers.Binary;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Frames bounded messages between the service and its desktop helper.</summary>
public static class DesktopPipe
{
    private const int UnchangedFrameRefreshMilliseconds = 5000;
    private const int MaximumDetailCharacters = 1000;

    public sealed record CaptureQuality(int JpegQuality, int MaxWidth);
    public static CaptureQuality Quality(string? preset) => preset switch
    {
        "low" => new(40, 1280),
        "balanced" => new(65, 1600),
        "high" => new(85, 2560),
        _ => throw new IOException("Unknown remote viewing quality.")
    };

    public static int FrameDelay(string? fps) => fps switch
    {
        "2" => 500, "4" => 250, "8" => 125, "16" => 63,
        _ => throw new IOException("Unsupported remote frame rate.")
    };

    // Session-local, capture-thread-only state. Compare visible pixels before JPEG encoding.
    public sealed class FrameChanges
    {
        private byte[] row = [];
        private byte[]? previous;
        private string? previousContext;
        private long lastFrame;
        public bool ShouldSend(nint pixels, int stride, int rowBytes, int height, string context)
        {
            if (row.Length < rowBytes) row = new byte[rowBytes];
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels + y * stride, row, 0, rowBytes);
                hash.AppendData(row, 0, rowBytes);
            }
            var current = hash.GetHashAndReset();
            var now = Environment.TickCount64;
            if (previousContext == context && previous is not null && current.AsSpan().SequenceEqual(previous) && now - lastFrame < UnchangedFrameRefreshMilliseconds)
                return false;
            previous = current; previousContext = context; lastFrame = now;
            return true;
        }
    }

    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int MaxInputBytes = 128 * 1024;
    public const int MaxClipboardCharacters = 16384;

    public static byte[] ControlMessage(object value) => "CTL\n"u8.ToArray().Concat(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))).ToArray();
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var data = await ReadAsync(stream, MaxFrameBytes, ct);
        if (data.AsSpan().StartsWith("ERR\n"u8))
            throw new IOException("Desktop helper: " + System.Text.Encoding.UTF8.GetString(data.AsSpan(4)));
        return data;
    }

    public static Task WriteErrorAsync(Stream stream, Exception error, CancellationToken ct)
    {
        var detail = $"{error.GetType().Name}: {error.Message}";
        if (detail.Length > MaximumDetailCharacters) detail = detail[..MaximumDetailCharacters];
        return WriteAsync(stream, System.Text.Encoding.UTF8.GetBytes("ERR\n" + detail), ct);
    }
    public static async Task<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximum) throw new InvalidDataException("Invalid desktop message length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }
    public static async Task WriteAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }
}
