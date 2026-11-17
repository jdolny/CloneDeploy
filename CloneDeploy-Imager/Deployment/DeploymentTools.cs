using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Deployment;

/// <summary>
/// Runs deployment tools with streamed input, progress parsing, cancellation, and diagnostic capture.
/// </summary>
internal sealed class DeploymentTools(DiagnosticLog log)
{
    private const int BrokenPipeHResult = unchecked((int)0x8007006D);
    private const int NoDataHResult = unchecked((int)0x800700E8);

    private const int MaximumLogTailLength = 64 * 1024;
    private const int OutputBufferCharacters = 4096;

    public async Task RunDismAsync(string[] arguments, CancellationToken ct, Action<double>? progress = null)
    {
        var phase = arguments.Contains("/Get-ImageInfo") ? "inspect" : "apply";
        var path = Path.ChangeExtension(log.FilePath, $".{phase}.dism.log");
        log.Write($"FFU {phase} DISM log: {path}");
        try
        {
            await RunAsync(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                [.. arguments, "/LogPath:" + path], ct, progress: progress, outputLine: null);
        }
        catch
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (file.Length > MaximumLogTailLength) file.Position = file.Length - MaximumLogTailLength;
                using var reader = new StreamReader(file);
                log.Write($"DISM {phase} log tail:\n" + await reader.ReadToEndAsync());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log.Failure("Reading DISM diagnostics", ex); }
            throw;
        }
    }

    public async Task RunAsync(string executable, string[] arguments, CancellationToken ct,
        Func<Stream, CancellationToken, Task>? input = null, Action<double>? progress = null, Action<string>? outputLine = null)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        log.Write("Deployment tool: " + Path.GetFileName(executable));
        process.Start();
        var output = Drain(process.StandardOutput, progress, outputLine);
        var error = Drain(process.StandardError, progress, outputLine);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var registration = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        Exception? inputFailure = null;
        async Task Pump()
        {
            if (input is null) return;
            try
            {
                var destination = new ProcessInputStream(process.StandardInput.BaseStream);
                await input(destination, stop.Token);
                await destination.FlushAsync(stop.Token);
                if (destination.ClosedEarly) log.Write("Deployment tool closed stdin early; remaining image input was consumed and verified.");
                try { process.StandardInput.Close(); }
                catch (IOException exception) when (ProcessInputStream.IsClosedPipe(exception)) { }
            }
            catch (Exception exception) { inputFailure = exception; stop.Cancel(); }
        }
        var pump = Pump();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            if (process.ExitCode != 0) stop.Cancel();
            await pump;
            await Task.WhenAll(output, error);
            log.Write($"Deployment tool {Path.GetFileName(executable)} exited with code {process.ExitCode}.");
            if (process.ExitCode != 0 || inputFailure is not null || arguments.Contains("/Get-ImageInfo") ||
                Path.GetFileName(executable).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                log.Write(output.Result + "\n" + error.Result);
            }
            ct.ThrowIfCancellationRequested();
            // Preserve network/checksum failures, but do not let cancellation caused
            // by a failed child process hide that child's exit code and diagnostics.
            if (inputFailure is not null && (inputFailure is not OperationCanceledException || process.ExitCode == 0))
                ExceptionDispatchInfo.Capture(inputFailure).Throw();
            if (process.ExitCode != 0)
            {
                if (arguments.Contains("/Get-ImageInfo")) throw new InvalidOperationException($"FFU inspection failed (exit {process.ExitCode}). No disk apply was performed.");
                throw new InvalidOperationException($"Deployment tool {Path.GetFileName(executable)} failed (exit {process.ExitCode}). The disk may be partially deployed; do not boot it as a completed deployment.");
            }
        }
        finally
        {
            stop.Cancel();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
        }
    }
    /// <summary>
    /// Handles a tool closing its input early so remaining source bytes can still be downloaded and verified.
    /// </summary>
    private sealed class ProcessInputStream(Stream destination) : Stream
    {
        // Pipable WIM apply may finish before consuming trailing metadata. Continue
        // consuming the producer so its full size/hash checks still run. The caller
        // separately requires a zero process exit code before accepting deployment.
        public bool ClosedEarly { get; private set; }
        public static bool IsClosedPipe(IOException exception) => exception.HResult is BrokenPipeHResult or NoDataHResult;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ClosedEarly) return;
            try { await destination.WriteAsync(buffer, ct); }
            catch (IOException exception) when (IsClosedPipe(exception)) { ClosedEarly = true; }
        }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override async Task FlushAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ClosedEarly) return;
            try { await destination.FlushAsync(ct); }
            catch (IOException exception) when (IsClosedPipe(exception)) { ClosedEarly = true; }
        }
    }
    private static async Task<string> Drain(StreamReader reader, Action<double>? progress, Action<string>? outputLine)
    {
        var tail = new StringBuilder();
        var line = new StringBuilder();
        var truncated = false;
        var buffer = new char[OutputBufferCharacters];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > MaximumLogTailLength) tail.Remove(0, tail.Length - MaximumLogTailLength);
            foreach (var c in buffer.AsSpan(0, count))
            {
                if (c is '\r' or '\n')
                {
                    if (!truncated) outputLine?.Invoke(line.ToString());
                    var match = Regex.Match(line.ToString(), @"(?<![\d.])(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
                    if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and <= 100) progress?.Invoke(Math.Min(99, percent));
                    line.Clear();
                    truncated = false;
                }
                else if (line.Length < OutputBufferCharacters) line.Append(c);
                else truncated = true;
            }
        }
        if (line.Length > 0 && !truncated) outputLine?.Invoke(line.ToString());
        return tail.ToString();
    }
}
