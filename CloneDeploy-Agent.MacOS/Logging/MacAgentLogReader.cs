using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Reads the service output captured by launchd, never a caller-supplied path.</summary>
public sealed class MacAgentLogReader(string path = "/Library/Logs/CloneDeploy/agent.log")
{
    private const int LogTailBytes = 32768;

    private readonly object sync = new();
    private readonly Dictionary<Guid, (byte[] Bytes, DateTimeOffset Expires)> downloads = new();

    public AgentLogReply Read(AgentLogRequest request, Guid deviceGuid)
    {
        var now = DateTimeOffset.UtcNow;
        if (request.RequestId == Guid.Empty || deviceGuid == Guid.Empty || request.DeviceGuid != deviceGuid
            || request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.AddMinutes(1))
            return new(request.RequestId, false, "", "The log request is invalid or expired.");
        lock (sync)
        {
            foreach (var stale in downloads.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray())
                downloads.Remove(stale);
            try
            {
                if (request.DownloadId is { } id)
                {
                    if (id == Guid.Empty || request.Offset < 0)
                        return new(request.RequestId, false, "", "Invalid log download request.");
                    if (request.Offset == 0 && !downloads.ContainsKey(id))
                    {
                        if (downloads.Count >= 4)
                            return new(request.RequestId, false, "", "Too many log downloads. Please try again shortly.");
                        using var file = Open();
                        if (file.Length > AgentLogReply.MaxDownloadBytes)
                            return new(request.RequestId, false, "", "The agent log exceeds the download size limit.");
                        var bytes = new byte[(int)file.Length];
                        file.ReadExactly(bytes);
                        downloads.Add(id, (bytes, now.AddMinutes(2)));
                    }
                    if (!downloads.TryGetValue(id, out var snapshot) || request.Offset > snapshot.Bytes.Length)
                        return new(request.RequestId, false, "", "The log download expired. Please try again.");
                    var data = snapshot.Bytes.AsSpan(request.Offset, Math.Min(AgentLogReply.ChunkBytes, snapshot.Bytes.Length - request.Offset)).ToArray();
                    if (request.Offset + data.Length == snapshot.Bytes.Length) downloads.Remove(id);
                    return new(request.RequestId, true, "", "Current Mac agent service log.") { Data = data, TotalBytes = snapshot.Bytes.Length };
                }

                using var stream = Open();
                // Bound reads even if launchd is continuously appending to a large log.
                var end = stream.Length;
                var length = (int)Math.Min(end, LogTailBytes);
                var offset = end - length;
                stream.Position = offset;
                var buffer = new byte[length];
                stream.ReadExactly(buffer);
                var text = Encoding.UTF8.GetString(buffer);
                if (text.Length > AgentLogReply.MaxCharacters)
                {
                    text = text[^AgentLogReply.MaxCharacters..];
                    offset = Math.Max(offset, 1);
                }
                if (offset > 0)
                {
                    var newline = text.IndexOf('\n');
                    if (newline >= 0 && newline < text.Length - 1) text = text[(newline + 1)..];
                    if (text.Length > 0 && char.IsLowSurrogate(text[0])) text = text[1..];
                }
                return new(request.RequestId, true, text, "Latest Mac agent service log entries.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(request.RequestId, false, "", "The agent log could not be read.");
            }
        }
    }

    private FileStream Open() => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
