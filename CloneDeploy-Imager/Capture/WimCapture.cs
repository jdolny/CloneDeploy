using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Discovery;

namespace CloneDeploy_Imager.Imaging;

/// <summary>
/// Streams an offline Windows capture over SMB or HTTPS and verifies it before publication.
/// </summary>
internal sealed class WimCapture(ImagingClient client, DiagnosticLog log)
{
    private const int MaximumLogTailLength = 64 * 1024;
    private const int OutputBufferCharacters = 4096;

    public CaptureResult? Result { get; private set; }
    internal Func<SmbCapture, (string Path, IDisposable Connection)> ConnectSmb { get; init; } = WimSmbConnection.Connect;

    public async IAsyncEnumerable<DeploymentProgress> RunAsync(int imageId, WindowsVolume source, string executable,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Result = null;
        ct.ThrowIfCancellationRequested();
        if (source.RunningWindows || source.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Boot WinPE to capture an offline Windows installation.");
        if (!File.Exists(Path.Combine(source.Root, "Windows", "System32", "config", "SYSTEM")))
            throw new InvalidOperationException("The selected Windows partition is no longer accessible.");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidOperationException("wimlib-imagex.exe is missing. Rebuild WinPE with its native tools, or configure WimlibPath for a development run.");
        yield return new(DeploymentStage.Preparing, null) { Status = "Requesting WIM upload destination" };
        var upload = await client.BeginCaptureAsync(imageId, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = stop.Token;
        await using var reporter = new CaptureTaskReporter(client, upload.Id, log, ct, stop);
        log.Write($"Capture upload started: {upload.Id}.");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        Task<string>? errors = null;
        var progress = new WimCaptureProgress();
        var reportedPercent = -1;
        var started = false;
        IDisposable? connection = null;
        string? smbPath = null;
        Task<string>? output = null;
        try
        {
            if (upload.Format != "Wim") throw new InvalidDataException("Invalid WIM capture destination.");
            if (upload.Direct is { } direct)
            {
                WimSmbConnection.Validate(direct);
                if (direct.RelativePath != $"Images\\{imageId}\\{upload.Id:N}\\capture.wim")
                    throw new InvalidDataException("Invalid WIM capture path.");
                var destination = ConnectSmb(direct);
                connection = destination.Connection;
                smbPath = destination.Path;
                if (File.Exists(smbPath)) throw new InvalidOperationException("The capture destination already exists. Start a new capture.");
            }
            log.Write("WIM capture transport: " + (smbPath is null ? "Https" : "Smb"));
            ct.ThrowIfCancellationRequested();
            yield return reporter.Update(new(DeploymentStage.Preparing, null) { Status = "Scanning Windows files and preparing WIM capture" });
            foreach (var argument in new[] { "capture", source.Root, smbPath ?? "-", "Windows", "--compress=fast", "--pipable" })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            started = true;
            errors = DrainErrors(process.StandardError, progress);
            // Stay preparing until wimlib reports actual file-data archiving.
            long size = 0;
            if (smbPath is not null)
            {
                // With a file destination, wimlib writes progress to stdout instead of binary WIM data.
                var directProgress = new WimCaptureProgress();
                output = DrainErrors(process.StandardOutput, directProgress);
                var completion = process.WaitForExitAsync(ct);
                while (!completion.IsCompleted)
                {
                    var percent = Math.Max(progress.Percent, directProgress.Percent);
                    if (percent > reportedPercent)
                    {
                        if (reportedPercent < 0)
                            yield return reporter.Update(new(DeploymentStage.Preparing, 100, true) { Status = "WIM capture source ready" });
                        reportedPercent = percent;
                        yield return reporter.Update(new(DeploymentStage.Deploying, percent) { Status = "Capturing Windows files directly to SMB" });
                    }
                    await Task.WhenAny(completion, Task.Delay(500, ct));
                    ct.ThrowIfCancellationRequested();
                }
                await completion;
            }
            else
            {
                var buffer = new byte[upload.ChunkBytes];
                while (true)
                {
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(count), ct);
                        var percent = progress.Percent;
                        if (percent > reportedPercent)
                        {
                            if (reportedPercent < 0)
                                yield return reporter.Update(new(DeploymentStage.Preparing, 100, true) { Status = "WIM capture source ready" });
                            reportedPercent = percent;
                            yield return reporter.Update(new(DeploymentStage.Deploying, percent) { Status = "Capturing and uploading Windows files" });
                        }
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == 0) break;
                    await client.UploadChunkAsync(upload.Id, size, buffer, count, ct);
                    size += count;
                    var uploadedPercent = progress.Percent;
                    if (uploadedPercent > reportedPercent)
                    {
                        if (reportedPercent < 0)
                            yield return reporter.Update(new(DeploymentStage.Preparing, 100, true) { Status = "WIM capture source ready" });
                        reportedPercent = uploadedPercent;
                        yield return reporter.Update(new(DeploymentStage.Deploying, uploadedPercent) { Status = "Capturing and uploading Windows files" });
                    }
                }
            }
            await process.WaitForExitAsync(ct);
            var diagnostic = await errors;
            if (output is not null) diagnostic += await output;
            log.Write($"wimlib capture exited with code {process.ExitCode}.");
            if (process.ExitCode != 0)
            {
                log.Write("wimlib capture diagnostics: " + diagnostic);
                throw new InvalidOperationException($"Windows capture failed (wimlib exit code {process.ExitCode}). No image version was published.");
            }
            if (smbPath is not null) size = new FileInfo(smbPath).Length;
            log.Write($"WIM capture uploaded {size} bytes.");
            // Empty captures or tools with no recognized progress still finish in stage order.
            if (reportedPercent < 0)
                yield return reporter.Update(new(DeploymentStage.Preparing, 100, true) { Status = "WIM capture source ready" });
            yield return reporter.Update(new(DeploymentStage.Deploying, 100, true) { Status = "WIM upload complete" });
            yield return reporter.Update(new(DeploymentStage.Finalizing, null) { Status = "Server SHA-256 calculation and publication" });
            Result = await client.CompleteCaptureAsync(upload.Id, size, ct);
            log.Write($"Capture upload completed: {upload.Id}, version {Result.Number}.");
            yield return new(DeploymentStage.Finalizing, 100, true);
        }
        finally
        {
            if (started)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                await process.WaitForExitAsync(CancellationToken.None);
                if (errors is not null) await errors;
                if (output is not null) await output;
            }
            try { connection?.Dispose(); }
            catch (Exception ex) { log.Failure("WIM capture connection cleanup", ex); }
            if (Result is null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await client.AbortCaptureAsync(upload.Id, cleanup.Token, ct.IsCancellationRequested ? "Cancelled" : "Failed"); }
                catch (Exception ex) { log.Failure($"Incomplete upload {upload.Id}; server cleanup may be required", ex); }
            }
        }
    }

    private static async Task<string> DrainErrors(StreamReader reader, WimCaptureProgress progress)
    {
        var text = new StringBuilder();
        var buffer = new char[OutputBufferCharacters];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            progress.Append(buffer.AsSpan(0, read));
            text.Append(buffer, 0, read);
            if (text.Length > MaximumLogTailLength) text.Remove(0, text.Length - MaximumLogTailLength);
        }
        progress.FinishLine();
        return text.ToString();
    }
}
