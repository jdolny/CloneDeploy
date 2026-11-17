using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Imaging;

/// <summary>
/// Runs FFU capture and inspection through DISM, reporting progress and recording tool diagnostics.
/// </summary>
internal sealed partial class FfuCaptureTool(DiagnosticLog log)
{
    private const int ProcessPollMilliseconds = 250;

    private const int Win32ErrorCodeMask = 0xffff;
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;

    private const int MaximumLogTailLength = 64 * 1024;
    private const int OutputBufferCharacters = 4096;
    public string StandardOutput { get; private set; } = "";

    // Only DISM's bracketed progress records count; output may use CR or arrive fragmented.
    internal static double? Percent(string line) => ProgressPattern().Match(line) is { Success: true } match &&
        double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value <= 100 ? value : null;
    [GeneratedRegex(@"^\s*\[[=\s]*([0-9]{1,3}(?:\.[0-9]+)?)%[=\s]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();

    public async IAsyncEnumerable<double?> RunAsync(string executable, IReadOnlyList<string> arguments, string operation,
        [EnumeratorCancellation] CancellationToken ct, string? scratchDirectory = null)
    {
        StandardOutput = "";
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new InvalidOperationException("DISM is missing from this WinPE image.");
        using var process = new Process { StartInfo = new(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        var dismLog = scratchDirectory is null ? Path.ChangeExtension(log.FilePath, $".capture-{operation}.dism.log") : Path.Combine(scratchDirectory, "dism-" + operation + ".log");
        process.StartInfo.ArgumentList.Add("/LogPath:" + dismLog);
        if (scratchDirectory is not null)
        {
            Directory.CreateDirectory(scratchDirectory);
            process.StartInfo.ArgumentList.Add("/ScratchDir:" + scratchDirectory);
            process.StartInfo.Environment["TEMP"] = scratchDirectory;
            process.StartInfo.Environment["TMP"] = scratchDirectory;
        }
        var progress = new ToolProgress();
        log.Write($"Starting DISM {operation}.");
        process.Start();
        var output = Drain(process.StandardOutput, progress);
        var errors = Drain(process.StandardError, null);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            yield return null;
            double? last = null;
            while (!exit.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                var value = Volatile.Read(ref progress.Value);
                if (value >= 0 && (last is null || value > last)) { last = value; yield return Math.Min(99, value); }
                await Task.WhenAny(exit, Task.Delay(ProcessPollMilliseconds, ct));
            }
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(output, errors);
            StandardOutput = output.Result;
            log.Write($"DISM {operation} exited with code {process.ExitCode}.");
            if (process.ExitCode != 0)
            {
                log.Write($"DISM {operation} diagnostics: {output.Result}\n{errors.Result}");
                var diskFull = (process.ExitCode & Win32ErrorCodeMask) is ErrorDiskFull or ErrorHandleDiskFull;
                throw new InvalidOperationException($"FFU {operation} failed (DISM exit code {process.ExitCode}). " +
                    (diskFull ? "DISM ran out of disk space. Its temporary workspace needs local storage; the image destination does not replace it. " : "") +
                    "No image version was published. See the Imager and DISM logs.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await exit;
            await Task.WhenAll(output, errors);
            try
            {
                if (process.ExitCode != 0 && File.Exists(dismLog))
                {
                    using var file = new FileStream(dismLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (file.Length > MaximumLogTailLength) file.Position = file.Length - MaximumLogTailLength;
                    using var reader = new StreamReader(file);
                    log.Write($"DISM {operation} log tail:\n" + await reader.ReadToEndAsync());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Failure("Reading capture DISM diagnostics", ex); }
        }
    }
    /// <summary>
    /// Holds the latest DISM percentage shared between output parsing and progress reporting.
    /// </summary>
    private sealed class ToolProgress { public double Value = -1; }
    private static async Task<string> Drain(StreamReader reader, ToolProgress? progress)
    {
        var tail = new StringBuilder();
        var line = new StringBuilder();
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
                    if (progress is not null && Percent(line.ToString()) is { } value && value > progress.Value) Volatile.Write(ref progress.Value, value);
                    line.Clear();
                }
                else if (line.Length < OutputBufferCharacters) line.Append(c);
            }
        }
        return tail.ToString();
    }
}
