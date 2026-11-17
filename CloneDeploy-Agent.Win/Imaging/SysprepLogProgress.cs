using System.Text;

namespace CloneDeploy_Agent;

// Read only newly written log data, with bounded reads and sharing compatible with Sysprep.
internal sealed class SysprepLogProgress(string path)
{
    private long offset = InitialLength(path);
    private static long InitialLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public string? ReadLatest()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset) offset = 0; // Sysprep may truncate its log at startup.
            if (stream.Length == offset) return null;
            Span<byte> bom = stackalloc byte[3];
            var header = stream.Read(bom);
            var encoding = header >= 2 && bom[0] == 0xFF && bom[1] == 0xFE ? Encoding.Unicode
                : header >= 2 && bom[0] == 0xFE && bom[1] == 0xFF ? Encoding.BigEndianUnicode : Encoding.UTF8;
            var position = Math.Max(offset, stream.Length - 16384);
            if (encoding != Encoding.UTF8) position -= position % 2;
            stream.Position = position;
            var buffer = new byte[Math.Min(16384, stream.Length - position)];
            var count = stream.Read(buffer);
            var text = encoding.GetString(buffer, 0, count).TrimStart('\uFEFF');
            offset = stream.Position;
            var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(line) ? null : line[..Math.Min(line.Length, 1000)];
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
