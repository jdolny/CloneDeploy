using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager;

internal sealed partial class ImagerApplication
{
    private const int MaximumCaptureHandoffBytes = 16384;

    private async Task<string?> TryPreparedCapture(ImagingClient client, CancellationToken ct)
    {
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) return null;
        var sources = inventory.WindowsVolumes.Where(x => !x.RunningWindows
            && File.Exists(Path.Combine(x.Root, PreparedCaptureHandoff.DirectoryName, PreparedCaptureHandoff.FileName))).ToArray();
        if (sources.Length == 0) return null;
        if (sources.Length != 1) throw new InvalidOperationException("Multiple prepared capture volumes were found. Resolve the stale capture before continuing.");
        var source = sources[0];
        var folder = Path.Combine(source.Root, PreparedCaptureHandoff.DirectoryName);
        var path = Path.Combine(folder, PreparedCaptureHandoff.FileName);
        RejectCaptureLinks(path);
        if (new FileInfo(path).Length > MaximumCaptureHandoffBytes) throw new InvalidDataException("Capture handoff is too large.");
        var handoff = JsonSerializer.Deserialize<PreparedCaptureHandoff>(await File.ReadAllTextAsync(path, ct))
            ?? throw new InvalidDataException("Capture handoff is missing.");
        await client.PrepareCaptureAsync(handoff, ct);
        try
        {
            var incompatible = CaptureCompatibility.UnavailableReason(handoff.Format, inventory);
            if (incompatible is not null) throw new InvalidOperationException(incompatible);
            var disk = handoff.Format == "Ffu" ? CaptureCompatibility.FfuDisks(inventory).SingleOrDefault(x => x.Number == source.DiskNumber) : null;
            if (handoff.Format == "Ffu" && disk is null) throw new InvalidOperationException("The prepared Windows disk is not eligible for FFU capture.");
            await ClearPreparedCaptureBootAsync(source.DiskNumber, ct);
            PreparedCaptureAgentCleanup.Clear(source.Root, handoff.UploadId);
            log.Write("Removed prepared capture agent boot journal and diagnostic logs from the source Windows volume.");
            // WinPE is running from RAM. Remove the upload capability and large staging files before capturing either format.
            foreach (var name in new[] { PreparedCaptureHandoff.FileName, "boot.wim", "boot.sdi" })
            {
                var file = Path.Combine(folder, name);
                RejectCaptureLinks(file);
                File.Delete(file);
            }
            Directory.Delete(folder); // Fail closed if unexpected files remain.
            ui.Heading();
            ui.Line($"Capturing a new version of {handoff.ImageName} ({handoff.Format}) from the prepared Windows disk.");
            if (disk is not null)
            {
                // Use the existing streaming transport; no scratch workspace on the source disk.
                var capture = new FfuCapture(client, log, direct =>
                {
                    var connection = SmbCaptureConnection.Connect(direct, direct.Username!, direct.Password!);
                    return (connection.FilePath, connection);
                });
                await ui.ShowProgressAsync(capture.RunAsync(handoff.ImageId, disk, Path.Combine(Environment.SystemDirectory, "dism.exe"), ct), ct, capture: true);
            }
            else
            {
                var capture = new WimCapture(client, log);
                await ui.ShowProgressAsync(capture.RunAsync(handoff.ImageId, source, settings.WimlibPath, ct), ct, capture: true);
            }
            ClearCapturedAnswerFiles(source);
            await UploadCompletedCaptureLog(client);
            return await CompleteImaging("Image uploaded successfully.", ct);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await client.AbortCaptureAsync(handoff.UploadId, cleanup.Token, "Failed"); }
            catch (Exception ex) { log.Failure("Cancel prepared capture", ex); }
            throw;
        }
        finally { client.SignOut(); }
    }

    private async Task ClearPreparedCaptureBootAsync(uint diskNumber, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"capture-boot-cleanup-{Guid.NewGuid():N}.ps1");
        try
        {
            using var resource = typeof(ImagerApplication).Assembly.GetManifestResourceStream("CloneDeploy.Clear-PreparedCaptureBoot.ps1")
                ?? throw new InvalidOperationException("Capture boot cleanup script is missing.");
            using var reader = new StreamReader(resource);
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(ct), ct);
            await new CloneDeploy_Imager.Deployment.DeploymentTools(log).RunAsync(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                    "-DiskNumber", diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
        }
        finally { File.Delete(scriptPath); }
    }

    private static void RejectCaptureLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Capture staging cannot contain file or directory links.");
    }
}
