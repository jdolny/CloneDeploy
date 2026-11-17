using System.Text.Json;

namespace CloneDeploy_Imager.Diagnostics;

/// <summary>
/// Writes structured local diagnostics and retains a bounded recent log for server failure reports.
/// </summary>
internal sealed class DiagnosticLog : IDisposable
{
    private const int MaximumRecentCharacters = 128 * 1024;

    private readonly TextWriter writer;
    private readonly object gate = new();
    private bool unavailable;
    private readonly System.Text.StringBuilder recent = new();
    public string Snapshot() { lock (gate) return recent.ToString(); }
    public string FilePath { get; }

    public DiagnosticLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"imager-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    internal DiagnosticLog(TextWriter writer, string filePath) { this.writer = writer; FilePath = filePath; }

    // Never pass credentials, HTTP bodies, headers, or command arguments.
    public void Write(string message)
    {
        lock (gate)
        {
            var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, message });
            recent.AppendLine(line);
            if (recent.Length > MaximumRecentCharacters) recent.Remove(0, recent.Length - MaximumRecentCharacters);
            if (unavailable) return;
            try { writer.WriteLine(line); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable = true; }
        }
    }

    public void Failure(string operation, Exception exception) =>
        Write($"{operation}: {exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}\n{exception.StackTrace}");

    public void Dispose()
    {
        lock (gate)
        {
            try { writer.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable = true; }
        }
    }
}
