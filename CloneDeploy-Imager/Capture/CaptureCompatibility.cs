using CloneDeploy_Imager.Discovery;

namespace CloneDeploy_Imager.Imaging;

/// <summary>
/// Determines eligible capture sources and explains why an image format cannot be captured.
/// </summary>
internal static class CaptureCompatibility
{
    public static DiskInfo[] FfuDisks(HardwareInventory inventory) => inventory.Disks
        .Where(x => (DiskPartitionStyle)x.PartitionStyle == DiskPartitionStyle.Gpt && !x.Offline && x.Size > 0 &&
            !inventory.WindowsVolumes.Any(v => v.DiskNumber == x.Number && v.RunningWindows)).ToArray();

    public static string? UnavailableReason(string format, HardwareInventory inventory)
    {
        if (!inventory.IsWinPe) return "Boot WinPE to capture an offline installation.";
        return format switch
        {
            "Wim" => inventory.WindowsVolumes.Any(x => !x.RunningWindows) ? null :
                "No accessible offline Windows partition. Check that the volume is unlocked.",
            "Ffu" => FfuDisks(inventory).Length > 0 ? null :
                "Requires an online GPT source disk; MBR and RAW disks are unsupported.",
            _ => "Unsupported image type in this Imager version."
        };
    }
}
