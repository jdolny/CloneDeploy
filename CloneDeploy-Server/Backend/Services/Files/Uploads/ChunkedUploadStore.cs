using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services.Files;

// Sessions are local to this process. Multiple app instances require sticky routing/shared storage.
/// <summary>
/// Receives uploads in smaller pieces and manages their completion or cancellation.
/// </summary>
public sealed class ChunkedUploadStore : BackgroundService
{
    public const int ChunkSize = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Session> _sessions = new();
    private readonly Dictionary<Guid, Session> _completed = new();
    private readonly HashSet<Guid> _attaching = new();
    private readonly UploadOptions _options;
    private readonly string _root;
    private readonly ILogger<ChunkedUploadStore> _logger;

    public ChunkedUploadStore(IOptions<UploadOptions> options, IWebHostEnvironment environment,
        ILogger<ChunkedUploadStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _root = Path.Combine(Path.GetFullPath(_options.TempStoragePath, environment.ContentRootPath), "pending");
        Directory.CreateDirectory(_root);
    }

    public async Task<UploadSessionDto> StartAsync(string owner, UploadRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 ||
            request.FileName.Any(char.IsControl) || request.Size <= 0 || request.Size > _options.MaxFileSize)
            throw new UploadException(400, "Choose a nonempty file within the server size limit (maximum " + _options.MaxFileSize + " bytes).");

        var name = Path.GetFileName(request.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(name,
                @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new UploadException(400, "The filename is not supported. Rename the file and try again.");

        await _gate.WaitAsync(ct);
        try
        {
            if (_sessions.Count >= _options.MaxActiveUploads || _sessions.Values.Count(s => s.Owner == owner) >= 3)
                throw new UploadException(429, "Too many active uploads. Try again later.");
            var id = Guid.NewGuid();
            _sessions.Add(id, new Session(owner, name, request.Size));
            return new UploadSessionDto(id, ChunkSize);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(string owner, Guid id, long offset, Stream body, long? length, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = Find(owner, id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            ct = timeout.Token;
            var expected = (int)Math.Min(ChunkSize, session.Size - session.Offset);
            if (offset != session.Offset || expected == 0)
                throw new UploadException(409, "Unexpected chunk offset.");
            if (length.HasValue && length != expected)
                throw new UploadException(400, "Unexpected chunk size.");
            // Bounded buffering prevents a partial request from corrupting the stored file.
            var buffer = new byte[expected + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await body.ReadAsync(buffer.AsMemory(read), ct);
                if (count == 0) break;
                read += count;
            }

            if (read != expected) throw new UploadException(400, "Unexpected chunk size.");
            if (session.Offset == 0) Directory.CreateDirectory(_root);
            FileStream? opened = null;
            for (var attempt = 0; opened is null; attempt++)
            {
                try
                {
                    opened = new FileStream(PartialPath(id), session.Offset == 0 ? FileMode.OpenOrCreate : FileMode.Open,
                        FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                {
                    if (attempt >= 20)
                        throw new UploadException(409, "The temporary upload file is locked by another process. Please try again.");
                    await Task.Delay(100, ct);
                }
                catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new UploadException(409, "The temporary upload file was removed. Please select the file and upload it again.");
                }
            }

            await using var file = opened;
            file.Position = session.Offset;
            await file.WriteAsync(buffer.AsMemory(0, expected), ct);
            await file.FlushAsync(ct);
            session.Offset += expected;
            session.LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UploadResultDto> CompleteAsync(string owner, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = Find(owner, id);
            if (session.Offset != session.Size)
                throw new UploadException(409, "The upload is incomplete.");
            // Keep the staged payload in place until its owner attaches it.
            _completed[id] = session;
            session.LastActivity = DateTime.UtcNow;
            _sessions.Remove(id);
            return new UploadResultDto(id, session.FileName, session.Size);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync(string owner, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryGetValue(id, out var cancelled) && !_completed.TryGetValue(id, out cancelled)) return;
            if (cancelled.Owner != owner) throw new UploadException(404, "Upload session not found.");
            try
            {
                File.Delete(PartialPath(id));
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                // The abandoned-file sweep retries deletion after the external lock is released.
                _logger.LogWarning(ex, "Unable to remove cancelled upload {UploadId}; deferred to cleanup", id);
            }

            _sessions.Remove(id);
            _completed.Remove(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Session Find(string owner, Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Owner != owner)
            throw new UploadException(404, "Upload session not found.");
        return session;
    }

    // Resolve the path and filename on the server; the browser only supplies the upload ID.
    public async Task AttachAsync(string owner, Guid id, Func<string, string, Task> attach)
    {
        var result = await AttachAsync(owner, id, async (source, name) =>
        {
            await attach(source, name);
            return ServiceResult.Ok();
        });
        if (!result.Success)
            throw new UploadException(404, "Completed upload not found for this user.");
    }

    // A rejected attachment remains available for retry, just like a failed publication.
    public async Task<ServiceResult> AttachAsync(string owner, Guid id, Func<string, string, Task<ServiceResult>> attach)
    {
        Session session;
        await _gate.WaitAsync();
        try
        {
            if (!_completed.TryGetValue(id, out session!) || session.Owner != owner)
                return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.ChunkedUploadStore.CompletedUploadNotFoundForThisUser));

            _completed.Remove(id);
            _attaching.Add(id);
        }
        finally
        {
            _gate.Release();
        }

        var attached = false;
        try
        {
            var result = await attach(PartialPath(id), session.FileName);
            if (!result.Success)
                return result;

            attached = true;
            // Cleanup failure must not turn an already-published upload into a retryable one.
            try
            {
                File.Delete(PartialPath(id));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to remove attached upload {UploadId}; deferred to cleanup", id);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to remove attached upload {UploadId}; deferred to cleanup", id);
            }

            return ServiceResult.Ok();
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (!attached)
                {
                    session.LastActivity = DateTime.UtcNow;
                    _completed[id] = session;
                }

                _attaching.Remove(id);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private string PartialPath(Guid id) => Path.Combine(_root, $"upload-{id:N}.partial");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _gate.WaitAsync(stoppingToken);
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                foreach (var pair in _completed.Where(p => p.Value.LastActivity < cutoff).ToArray())
                {
                    File.Delete(PartialPath(pair.Key));
                    _completed.Remove(pair.Key);
                }

                foreach (var pair in _sessions.Where(p => p.Value.LastActivity < cutoff).ToArray())
                {
                    File.Delete(PartialPath(pair.Key));
                    _sessions.Remove(pair.Key);
                }

                // Includes abandoned files from a previous process run.
                foreach (var path in Directory.EnumerateFiles(_root, "upload-*.partial"))
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path)[7..], "N", out var id) &&
                        !_sessions.ContainsKey(id) && !_completed.ContainsKey(id) && !_attaching.Contains(id) &&
                        File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to clean up abandoned uploads");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to clean up abandoned uploads");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Tracks the owner, progress, and last activity of an upload in progress.
    /// </summary>
    private sealed class Session(string owner, string fileName, long size)
    {
        public string Owner { get; } = owner;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public long Offset { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }
}
