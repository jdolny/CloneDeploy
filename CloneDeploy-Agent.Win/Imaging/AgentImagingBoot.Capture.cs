using System.Management;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

public sealed partial class AgentImagingBoot
{
    private static string CaptureFolder => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, PreparedCaptureHandoff.DirectoryName);

    // Called while image prep owns the execution gate; never starts a second worker or restarts Windows.
    public async Task StageCaptureAsync(AgentPreparedCapture capture, Action<string> progress, CancellationToken ct)
    {
        if (!ready || File.Exists(Journal)) throw new InvalidOperationException("WinPE boot recovery is not complete. Check the agent log.");
        var plan = capture.Boot;
        ValidateArchitecture(plan.Architecture, RuntimeInformation.OSArchitecture);
        await CheckAsync(plan.ServerUrl, ct);
        if (capture.Format == "Ffu")
        {
            using var partitions = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_Partition");
            using var rows = partitions.Get();
            uint? disk = null;
            foreach (ManagementObject partition in rows)
            using (partition)
                if (Convert.ToChar(partition["DriveLetter"]) == Environment.SystemDirectory[0])
                    disk = Convert.ToUInt32(partition["DiskNumber"]);
            if (disk is null) throw new InvalidOperationException("Cannot identify the Windows disk for FFU capture.");
            using var disks = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT * FROM MSFT_Disk WHERE Number = {disk.Value}");
            using var diskRows = disks.Get();
            var gpt = false;
            foreach (ManagementObject row in diskRows)
            using (row) gpt = Convert.ToUInt32(row["PartitionStyle"]) == 2;
            if (!gpt) throw new InvalidOperationException("FFU capture requires a GPT Windows disk. Select a WIM image for this computer.");
        }
        if (plan.DeviceGuid != identity.State.DeviceGuid || plan.Files.Length != 2
            || !plan.Files.Select(x => x.Name).Order().SequenceEqual(new[] { "boot.sdi", "boot.wim" })
            || plan.Files.Any(x => x.Size <= 0 || x.Size > MaximumBootFileBytes || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid capture boot manifest.");
        var folder = CaptureFolder;
        PolicyActionRunner.RejectReparsePoints(folder);
        if (Directory.Exists(folder)) throw new InvalidOperationException("A previous capture staging folder remains. Restart the agent to recover it before retrying.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var boot = new PendingBoot(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()) { CaptureUploadId = capture.UploadId };
        await File.WriteAllTextAsync(Journal + ".new", JsonSerializer.Serialize(boot), ct);
        File.Move(Journal + ".new", Journal, true);
        new DirectoryInfo(folder).Create(acl);
        using var client = Client();
        if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < plan.Files.Sum(x => x.Size) + RequiredFreeSpaceReserveBytes)
            throw new IOException("Insufficient space to stage WinPE.");
        foreach (var file in plan.Files)
        {
            progress($"Downloading and verifying {file.Name}");
            using var response = await AgentResponseVerification.DownloadAsync(client, $"api/agent/v1/prepared-capture/{capture.UploadId}/files/{file.Name}", file.Sha256, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(Path.Combine(folder, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyVerifiedAsync(input, output, file, ct);
        }
        using var current = await client.GetAsync($"api/agent/v1/prepared-capture/{capture.UploadId}", ct);
        current.EnsureSuccessStatusCode();
        GetFirmwareType(out var firmware);
        foreach (var args in BootCommands(boot.Loader, boot.Ramdisk, folder, firmware == 2))
            await RunToolAsync("bcdedit.exe", args, ct);
        var handoff = new PreparedCaptureHandoff(capture.UploadId, capture.Token, plan.ServerUrl, plan.ImageId,
            plan.ImageName, capture.Format, capture.ExpiresAtUtc);
        await File.WriteAllTextAsync(Path.Combine(folder, PreparedCaptureHandoff.FileName), JsonSerializer.Serialize(handoff), ct);
    }

    public async Task CaptureReadyAsync(AgentPreparedCapture capture, CancellationToken ct)
    {
        await CheckAsync(capture.Boot.ServerUrl, ct);
        // Scripts may have changed encryption after the profile's decryption step.
        using var volumes = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
            "SELECT * FROM Win32_EncryptableVolume");
        using var found = volumes.Get();
        foreach (ManagementObject volume in found)
        using (volume)
        {
            if (capture.Format != "Ffu" && !string.Equals(Convert.ToString(volume["DriveLetter"]), Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            using var conversion = volume.InvokeMethod("GetConversionStatus", null, null);
            if (Convert.ToUInt32(conversion["ReturnValue"]) != 0 || Convert.ToUInt32(conversion["ConversionStatus"]) != 0)
                throw new InvalidOperationException("Capture volumes must be fully decrypted before automatic capture.");
        }
        using var client = Client();
        using var response = await client.PostAsync($"api/agent/v1/prepared-capture/{capture.UploadId}/ready", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task AbortCaptureAsync(Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var client = Client();
            using var response = await client.DeleteAsync($"api/agent/v1/prepared-capture/{id}", deadline.Token);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await DisarmCaptureAsync(options.StateDirectory, cleanup.Token, id); }
            catch { ready = false; throw; }
        }
    }

    internal static async Task ArmCaptureAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetFullPath(directory), "WinPe", "pending.json");
        var boot = JsonSerializer.Deserialize<PendingBoot>(await File.ReadAllTextAsync(path, ct));
        if (boot?.CaptureUploadId is null || !File.Exists(Path.Combine(CaptureFolder, PreparedCaptureHandoff.FileName)))
            throw new InvalidOperationException("Capture boot preparation is missing.");
        var handoff = JsonSerializer.Deserialize<PreparedCaptureHandoff>(await File.ReadAllTextAsync(Path.Combine(CaptureFolder, PreparedCaptureHandoff.FileName), ct));
        if (handoff?.UploadId != boot.CaptureUploadId || handoff.ExpiresAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("Capture boot preparation is expired or inconsistent.");
        await RunToolAsync("bcdedit.exe", ["/bootsequence", $"{{{boot.Loader}}}", "/addfirst"], ct);
    }

    internal static async Task DisarmCaptureAsync(string directory, CancellationToken ct, Guid? expectedUpload = null)
    {
        var path = Path.Combine(Path.GetFullPath(directory), "WinPe", "pending.json");
        if (!File.Exists(path)) return;
        var boot = JsonSerializer.Deserialize<PendingBoot>(await File.ReadAllTextAsync(path, ct));
        if (boot?.CaptureUploadId is null) return;
        if (expectedUpload is not null && boot.CaptureUploadId != expectedUpload) return;
        await CleanupAsync(boot, ct);
        DeleteCaptureFiles();
        File.Delete(path);
    }

    private static void DeleteCaptureFiles()
    {
        PolicyActionRunner.RejectReparsePoints(CaptureFolder);
        if (!Directory.Exists(CaptureFolder)) return;
        foreach (var name in new[] { "boot.wim", "boot.sdi", PreparedCaptureHandoff.FileName })
            File.Delete(Path.Combine(CaptureFolder, name));
        if (Directory.Exists(CaptureFolder)) Directory.Delete(CaptureFolder); // Never recursively remove unknown contents.
    }
}
