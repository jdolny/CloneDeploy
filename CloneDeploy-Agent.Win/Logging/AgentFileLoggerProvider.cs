using System.Globalization;
using System.Text;
using System.Security.Principal;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

// Uses the hosting framework's ILogger interfaces and BCL file APIs; no logging package required.
/// <summary>
/// Writes agent activity to local log files and rotates them to keep their size under control.
/// </summary>
public sealed class AgentFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const long DefaultLogFileBytes = 5 * 1024 * 1024;
    private const int MinimumLogFileBytes = 1024;

    private const int MaximumEntryCharacters = 16000;
    private const int LogTailBytes = 32768;

    private readonly object sync = new();
    private readonly string directory;
    private readonly string stateDirectory;
    private readonly long maxBytes;
    private readonly string? provisioningKey;
    private readonly SecurityIdentifier? logReader;
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    private DateTime nextError;
    private bool disposed;
    private bool directoryPrepared;
    private readonly Dictionary<Guid, (byte[] Bytes, DateTimeOffset Expires)> downloads = new();

    public string LogPath => Path.Combine(directory, "agent.log");

    public AgentFileLoggerProvider(AgentOptions options, long maxBytes = DefaultLogFileBytes)
    {
        if (maxBytes < MinimumLogFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        stateDirectory = options.StateDirectory;
        directory = AgentDiagnosticLogs.DirectoryFor(stateDirectory);
        this.maxBytes = maxBytes;
        provisioningKey = string.IsNullOrEmpty(options.ProvisioningKey) ? null : options.ProvisioningKey;

        if (!string.IsNullOrWhiteSpace(options.LogReaderSid))
        {
            logReader = new SecurityIdentifier(options.LogReaderSid);

            if (!logReader.IsAccountSid())
                throw new ArgumentException("LogReaderSid must identify a Windows account.");
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

    public void Dispose()
    {
        lock (sync)
            disposed = true;
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (sync)
        {
            if (disposed)
                return;

            try
            {
                var scopeText = new StringBuilder();
                scopes.ForEachScope((scope, text) => text.Append(" [").Append(scope).Append(']'), scopeText);
                var text = $"{message}{(exception is null ? "" : $"{Environment.NewLine}{exception}")}";

                if (provisioningKey is not null)
                    text = text.Replace(provisioningKey, "[redacted]", StringComparison.Ordinal);

                if (text.Length > MaximumEntryCharacters)
                    text = $"{text[..16000]} [truncated]";

                var timestamp = DateTimeOffset.UtcNow.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
                var prefix = $"{timestamp} [{level}]{scopeText} ";
                var entry = $"{prefix}{text.Replace("\r\n", "\n").Replace("\r", "\n")
                    .Replace("\n", $"{Environment.NewLine}{prefix}")}{Environment.NewLine}";

                if (Encoding.UTF8.GetByteCount(entry) > maxBytes)
                    entry = $"{entry[..(int)Math.Min( entry.Length, maxBytes / 4 - 40)]}{Environment.NewLine}[log entry truncated]{Environment.NewLine}";

                PrepareDirectory();

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length + Encoding.UTF8.GetByteCount(entry) > maxBytes)
                {
                    // Five files total: current file plus four archives.
                    var oldest = Path.Combine(directory, "agent.4.log");

                    if (File.Exists(oldest))
                        File.Delete(oldest);

                    for (var i = 3; i >= 1; i--)
                    {
                        var old = Path.Combine(directory, $"agent.{i}.log");

                        if (File.Exists(old))
                            File.Move(old, Path.Combine(directory, $"agent.{i + 1}.log"), true);
                    }

                    File.Move(LogPath, Path.Combine(directory, "agent.1.log"), true);
                }

                File.AppendAllText(LogPath, entry, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk failure must not stop policy execution or recursively call ILogger.
                if (DateTime.UtcNow >= nextError)
                {
                    nextError = DateTime.UtcNow.AddMinutes(1);
                    Console.Error.WriteLine($"Agent file logging unavailable ({ex.GetType().Name}).");
                }
            }
        }
    }

    private void PrepareDirectory()
    {
        if (directoryPrepared)
            return;

        AgentDiagnosticLogs.Prepare(stateDirectory, logReader);
        directoryPrepared = true;
    }

    public string ReadTail()
    {
        lock (sync)
        {
            var text = "";

            for (var i = 0; i <= 4 && text.Length < AgentLogReply.MaxCharacters; i++)
            {
                var path = i == 0 ? LogPath : Path.Combine(directory, $"agent.{i}.log");

                if (!File.Exists(path))
                    continue;

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var offset = Math.Max(0, stream.Length - LogTailBytes);
                stream.Position = offset;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var chunk = reader.ReadToEnd();

                if (offset > 0)
                {
                    var newline = chunk.IndexOf('\n');
                    // Keep the tail of a single long line instead of dropping it entirely.
                    if (newline >= 0 && newline < chunk.Length - 1)
                        chunk = chunk[(newline + 1)..];
                }

                text = $"{chunk}{text}";
            }

            if (text.Length > AgentLogReply.MaxCharacters)
            {
                text = text[^AgentLogReply.MaxCharacters..];
                var newline = text.IndexOf('\n');

                if (newline >= 0 && newline < text.Length - 1)
                    text = text[(newline + 1)..];

                if (text.Length > 0 && char.IsLowSurrogate(text[0]))
                    text = text[1..];
            }

            return text;
        }
    }

    public AgentLogReply Read(AgentLogRequest request, Guid deviceGuid)
    {
        if (request.RequestId == Guid.Empty || request.DeviceGuid != deviceGuid
            || request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new(request.RequestId, false, "", "The log request is invalid or expired.");

        try
        {
            if (request.DownloadId is { } downloadId)
            {
                lock (sync)
                {
                    foreach (var stale in downloads.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                        downloads.Remove(stale);
                    if (downloadId == Guid.Empty || request.Offset < 0)
                        return new(request.RequestId, false, "", "Invalid log download request.");
                    if (request.Offset == 0 && !downloads.ContainsKey(downloadId))
                    {
                        if (downloads.Count >= 4)
                            return new(request.RequestId, false, "", "Too many log downloads. Please try again shortly.");
                        using var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (file.Length > AgentLogReply.MaxDownloadBytes)
                            return new(request.RequestId, false, "", "The agent log exceeds the download size limit.");
                        var bytes = new byte[(int)file.Length];
                        file.ReadExactly(bytes);
                        downloads.Add(downloadId, (bytes, DateTimeOffset.UtcNow.AddMinutes(2)));
                    }
                    if (!downloads.TryGetValue(downloadId, out var snapshot) || request.Offset > snapshot.Bytes.Length)
                        return new(request.RequestId, false, "", "The log download expired. Please try again.");
                    var data = snapshot.Bytes.AsSpan(request.Offset, Math.Min(AgentLogReply.ChunkBytes, snapshot.Bytes.Length - request.Offset)).ToArray();
                    if (request.Offset + data.Length == snapshot.Bytes.Length) downloads.Remove(downloadId);
                    return new(request.RequestId, true, "", "Current agent log (UTC).") { Data = data, TotalBytes = snapshot.Bytes.Length };
                }
            }
            return new(request.RequestId, true, ReadTail(), "Latest agent log entries (UTC).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(request.RequestId, false, "", "The agent log could not be read.");
        }
    }

    /// <summary>
    /// Passes log messages from one part of the agent to the shared file writer.
    /// </summary>
    private sealed class FileLogger(AgentFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);

        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                owner.Write(category, level, formatter(state, exception), exception);
        }
    }
}
