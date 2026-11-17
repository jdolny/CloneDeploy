using System.Runtime.InteropServices;
using System.Text.Json;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Deployment;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using CloneDeploy_Imager.UI;

namespace CloneDeploy_Imager;

/// <summary>
/// Guides image selection and source validation before starting WIM or FFU capture.
/// </summary>
internal sealed partial class ImagerApplication
{
    private const int MaximumImageNameCharacters = 200;
    private const double BytesPerGiB = 1024d * 1024 * 1024;

    private async Task<string> Capture(ImagingClient client, CancellationToken ct)
    {
        if (client.Device is null) await LinkDevice(client, ct);
        if (client.Device is null) return "Main menu";
        var inventory = await new HardwareDiscovery().ReadAsync(ct);
        if (!inventory.IsWinPe) throw new InvalidOperationException("Boot WinPE to capture an offline Windows installation.");
        var action = await ui.ChooseAvailableAsync("Capture destination", [new("Upload to existing image", "Upload to existing image", client.Can("Image.View") ? null : "View images permission required"), new("Create new image", "Create new image", client.Can("Image.Create") ? null : "Create image permission required"), new("Cancel", "Cancel")], ct);
        if (action == "Cancel") return "Main menu";
        CaptureImage image;
        if (action == "Create new image")
        {
            var format = await ui.ChooseAvailableAsync("New image type", [
                new("Wim", "WIM - Windows partition", CaptureCompatibility.UnavailableReason("Wim", inventory)),
                new("Ffu", "FFU - whole GPT disk", CaptureCompatibility.UnavailableReason("Ffu", inventory)),
                new("Cancel", "Cancel")], ct);
            if (format == "Cancel") return "Main menu";
            var name = (await ui.AskAsync("New image name:", false, ct)).Trim();
            if (name.Length == 0) return "Main menu";
            if (name.Length > MaximumImageNameCharacters || name.Any(char.IsControl)) throw new InvalidOperationException("Enter an image name between 1 and 200 characters without control characters.");
            image = await client.CreateCaptureImageAsync(name, format, ct);
        }
        else
        {
            var images = await client.CaptureImagesAsync(ct);
            if (images.Count == 0) { ui.Line("No enabled, unprotected images are available. Choose Create new image to begin."); await ui.WaitForBackAsync(ct); return "Main menu"; }
            var selected = await ui.ChooseAvailableAsync("Upload to existing image", [.. images.Select(x =>
                new MenuChoice(x.Id.ToString(), $"{x.Id}: {x.Name} ({x.Format})", CaptureCompatibility.UnavailableReason(x.Format, inventory))),
                new("Cancel", "Cancel")], ct);
            if (selected == "Cancel") return "Main menu";
            image = images.Single(x => x.Id.ToString() == selected);
        }
        if (image.Format == "Ffu") return await CaptureFfu(client, image, inventory, ct);
        if (image.Format != "Wim") throw new InvalidOperationException("This image type is not supported by this Imager version.");
        var sources = inventory.WindowsVolumes.Where(x => !x.RunningWindows).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException("No accessible offline Windows partition was found. Check that the volume is unlocked.");
        var source = sources[0];
        if (sources.Length > 1)
        {
            ui.Line("Multiple Windows partitions found. Select one to upload.");
            var sourceChoices = sources.Select(x => $"Disk {x.DiskNumber}, partition {x.PartitionNumber}: {x.Root}").ToArray();
            var selectedSource = await ui.ChooseAsync("Windows partition to upload", [.. sourceChoices, "Cancel"], ct);
            if (selectedSource == "Cancel") return "Main menu";
            source = sources[Array.IndexOf(sourceChoices, selectedSource)];
        }
        if (!await ui.ConfirmUploadAsync(image.Name, ct)) return "Main menu";
        var capture = new WimCapture(client, log);
        ui.Heading();
        await ui.ShowProgressAsync(capture.RunAsync(image.Id, source, settings.WimlibPath, ct), ct, capture: true);
        ClearCapturedAnswerFiles(source);
        await UploadCompletedCaptureLog(client);
        return await CompleteImaging("Image uploaded successfully.", ct);
    }

    private async Task<string> CaptureFfu(ImagingClient client, CaptureImage image, HardwareInventory inventory, CancellationToken ct)
    {
        var disks = CaptureCompatibility.FfuDisks(inventory);
        if (disks.Length == 0) throw new InvalidOperationException("No online GPT disk was found. FFU capture requires GPT and does not support encrypted disks.");
        var choices = disks.Select(x => $"Disk {x.Number}: {x.Name}, {x.Size / BytesPerGiB:0.0} GiB, serial {x.Serial}").ToArray();
        var selected = await ui.ChooseAsync("Whole disk to capture as FFU", [.. choices, "Cancel"], ct);
        if (selected == "Cancel") return "Main menu";
        var disk = disks[Array.IndexOf(choices, selected)];
        if (settings.FfuScratchDirectory is { } scratch)
        {
            var root = Path.GetPathRoot(scratch)!;
            if (scratch.StartsWith(@"\\", StringComparison.Ordinal) || root.Length != 3 ||
                root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) ||
                new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new InvalidOperationException("FFU scratch must be on a local disk other than the WinPE RAM disk.");
            for (var current = Path.GetFullPath(scratch); current is not null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("FFU scratch cannot contain directory links.");
            var partitions = Wmi.Query(@"root\Microsoft\Windows\Storage", "MSFT_Partition", ["DiskNumber", "AccessPaths"], ct);
            var scratchPartition = partitions.SingleOrDefault(x => x["AccessPaths"] is Array access &&
                access.Cast<object>().Any(p => string.Equals(Convert.ToString(p), root, StringComparison.OrdinalIgnoreCase)));
            if (scratchPartition is null || Convert.ToUInt32(scratchPartition["DiskNumber"]) == disk.Number)
                throw new InvalidOperationException("FFU scratch must be on an identified local disk other than the source disk.");
        }
        if (!await ui.ConfirmUploadAsync(image.Name, ct)) return "Main menu";
        var capture = new FfuCapture(client, log, direct =>
        {
            var connection = SmbCaptureConnection.Connect(direct, direct.Username!, direct.Password!);
            return (connection.FilePath, connection);
        }, settings.FfuScratchDirectory);
        ui.Heading();
        await ui.ShowProgressAsync(capture.RunAsync(image.Id, disk, Path.Combine(Environment.SystemDirectory, "dism.exe"), ct), ct, capture: true);
        foreach (var source in inventory.WindowsVolumes.Where(x => x.DiskNumber == disk.Number && !x.RunningWindows))
            ClearCapturedAnswerFiles(source);
        await UploadCompletedCaptureLog(client);
        return await CompleteImaging("Image uploaded successfully.", ct);
    }
}
