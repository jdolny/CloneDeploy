using System.Runtime.CompilerServices;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.FfuFileSystem.Writing;

namespace CloneDeploy_Imager.Imaging;

/// <summary>
/// Coordinates whole-disk FFU capture, inspection, checksum verification, publication, and failed-upload cleanup.
/// </summary>
internal sealed class FfuCapture(ImagingClient client, DiagnosticLog log,
    Func<SmbCapture, (string Path, IDisposable Connection)> connect, string? scratchRoot = null)
{
    private const int StorageProbeBytes = 4096;

    private const int FileBufferBytes = 1024 * 1024;

    public CaptureResult? Result { get; private set; }
    public async IAsyncEnumerable<DeploymentProgress> RunAsync(int imageId, DiskInfo disk, string dism,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Result = null;
        if ((DiskPartitionStyle)disk.PartitionStyle != DiskPartitionStyle.Gpt || disk.Offline || disk.Size == 0)
            throw new InvalidOperationException("FFU capture requires an online GPT disk. Encrypted disks are not supported.");
        ct.ThrowIfCancellationRequested();
        yield return new(DeploymentStage.Preparing, null) { Status = "Checking FFU capture source and scratch space" };
        var scratch = scratchRoot is null ? null : Path.Combine(scratchRoot, "ffu-" + Guid.NewGuid().ToString("N"));
        if (scratch is not null)
        {
            Directory.CreateDirectory(scratch);
            // Fail before capture if the explicitly selected scratch disk is unwritable/full.
            await using var probe = new FileStream(Path.Combine(scratch, "probe.tmp"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, StorageProbeBytes, FileOptions.DeleteOnClose);
            await probe.WriteAsync(new byte[StorageProbeBytes], ct);
            await probe.FlushAsync(ct);
            log.Write("FFU DISM scratch and logs: " + scratch);
        }
        yield return new(DeploymentStage.Preparing, null) { Status = "Requesting FFU upload destination" };
        var upload = await client.BeginCaptureAsync(imageId, ct, "Ffu");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var reporter = new CaptureTaskReporter(client, upload.Id, log, stop.Token, stop);
        IDisposable? connection = null;
        FfuCaptureMount? mount = null;
        try
        {
            if (upload.Format != "Ffu")
                throw new InvalidDataException("Invalid FFU capture destination.");
            string path;
            if (upload.FfuTransport == "Https" && upload.Direct is null)
            {
                yield return reporter.Update(new(DeploymentStage.Preparing, null) { Status = "Connecting to HTTPS upload destination" });
                mount = new FfuCaptureMount(token => new HttpsFfuCaptureFile(client, upload.Id, token), log, stop.Token);
                connection = mount; path = mount.FilePath;
            }
            else
            {
                if (upload.FfuTransport is not (null or "Smb") || upload.Direct is not { } direct || direct.RelativePath != $"Images\\{imageId}\\{upload.Id:N}\\capture.ffu")
                    throw new InvalidDataException("Invalid FFU capture destination.");
                SmbCaptureConnection.Validate(direct);
                if (string.IsNullOrWhiteSpace(direct.Username) || direct.Password is null)
                    throw new InvalidOperationException("The server did not provide SMB credentials. Configure a managed credential on the storage provider and update the server.");
                yield return reporter.Update(new(DeploymentStage.Preparing, null) { Status = "Connecting to SMB upload destination" });
                var destination = connect(direct);
                connection = destination.Connection; path = destination.Path;
            }
            if (File.Exists(path)) throw new InvalidOperationException("The capture destination already exists. Start a new capture.");
            var tool = new FfuCaptureTool(log);
            yield return reporter.Update(new(DeploymentStage.Preparing, 100, true) { Status = "FFU capture destination ready" });
            yield return reporter.Update(new(DeploymentStage.Deploying, null) { Status = "Capturing and uploading FFU disk image" });
            await foreach (var percent in tool.RunAsync(dism,
                ["/English", "/Capture-FFU", "/ImageFile:" + path, $@"/CaptureDrive:\\.\PhysicalDrive{disk.Number}", "/Name:CloneDeploy", "/Compress:default"], "capture", stop.Token, scratch))
                yield return reporter.Update(new(DeploymentStage.Deploying, percent) { Status = "Capturing and uploading FFU disk image" });
            yield return reporter.Update(new(DeploymentStage.Deploying, 100, true) { Status = "FFU capture complete" });
            if (mount is not null)
            {
                yield return reporter.Update(new(DeploymentStage.Finalizing, null) { Status = "Flushing pending FFU uploads" });
                mount.FlushAndVerify();
            }
            yield return reporter.Update(new(DeploymentStage.Finalizing, null) { Status = "Inspecting captured FFU" });
            // Optimization is disabled while investigating WinPE finalization failures.
            log.Write("FFU optimization skipped; publishing an unoptimized capture.");
            await foreach (var _ in tool.RunAsync(dism, ["/English", "/Get-ImageInfo", "/ImageFile:" + path, "/Index:1"], "inspection", stop.Token, scratch)) { }
            log.Write("FFU inspection:\n" + tool.StandardOutput);
            var minimumDiskSizeBytes = FfuImageInfo.MinimumDiskSizeBytes(tool.StandardOutput);
            log.Write($"FFU minimum destination disk size: {minimumDiskSizeBytes} bytes.");
            long size;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes, true))
                size = file.Length;
            log.Write("Starting FFU server publication.");
            if (mount is not null)
            {
                mount.FlushAndVerify();
                mount.Dispose(); mount = null; connection = null;
            }
            yield return reporter.Update(new(DeploymentStage.Finalizing, null) { Status = "Server SHA-256 calculation and publication" });
            Result = await client.CompleteCaptureAsync(upload.Id, size, stop.Token, optimized: false, minimumDiskSizeBytes: minimumDiskSizeBytes);
            yield return new(DeploymentStage.Finalizing, 100, true);
        }
        finally
        {
            // Release filesystem callbacks before asking the server to remove unfinished files.
            try { connection?.Dispose(); }
            catch (Exception ex) { log.Failure("FFU capture connection cleanup", ex); }
            if (Result is null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await client.AbortCaptureAsync(upload.Id, cleanup.Token, stop.IsCancellationRequested ? "Cancelled" : "Failed"); }
                catch (Exception ex) { log.Failure($"Incomplete FFU upload {upload.Id}; server cleanup may be required", ex); }
            }
        }
    }
}
