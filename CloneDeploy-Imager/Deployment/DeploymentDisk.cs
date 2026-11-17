using System.Runtime.InteropServices;
using Microsoft.Win32;
using CloneDeploy_Imager.Discovery;

namespace CloneDeploy_Imager.Deployment;

/// <summary>Windows storage bus identifiers accepted for internal deployment disks.</summary>
internal enum StorageBusType : uint
{
    Scsi = 1, Ata = 3, Raid = 8, Sas = 10, Sata = 11, Virtual = 14, Nvme = 17, Scm = 18
}

/// <summary>
/// Describes a deployment disk, including the identity and state used to validate the target.
/// </summary>
internal sealed record DeploymentDisk(uint Number, string UniqueId, string Name, ulong Size, uint BusType, bool IsBoot, bool IsSystem, bool Offline, bool ReadOnly)
{
    private const ulong MinimumDeploymentDiskBytes = 8UL * 1024 * 1024 * 1024;

    public bool Eligible => !IsBoot && !IsSystem && !Offline && !ReadOnly && Size >= MinimumDeploymentDiskBytes &&
        !string.IsNullOrWhiteSpace(UniqueId) && (StorageBusType)BusType is StorageBusType.Scsi or StorageBusType.Ata or StorageBusType.Raid or
            StorageBusType.Sas or StorageBusType.Sata or StorageBusType.Virtual or StorageBusType.Nvme or StorageBusType.Scm;
}
/// <summary>
/// Discovers eligible deployment disks and checks their identity before imaging operations.
/// </summary>
internal static class DeploymentDisks
{
    private const uint FirmwareBios = 1;
    private const uint FirmwareUefi = 2;
    public static DeploymentDisk[] Read(CancellationToken ct)
    {
        using var pe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        if (pe is null) throw new InvalidOperationException("Deployment is available only in WinPE.");
        return Wmi.Query(@"root\Microsoft\Windows\Storage", "MSFT_Disk",
            ["Number", "UniqueId", "FriendlyName", "Size", "BusType", "IsBoot", "IsSystem", "IsOffline", "IsReadOnly"], ct)
            .Select(x => new DeploymentDisk(Convert.ToUInt32(x["Number"]), Convert.ToString(x["UniqueId"])?.Trim() ?? "",
                Convert.ToString(x["FriendlyName"]) ?? "", Convert.ToUInt64(x["Size"]), Convert.ToUInt32(x["BusType"]),
                Convert.ToBoolean(x["IsBoot"]), Convert.ToBoolean(x["IsSystem"]), Convert.ToBoolean(x["IsOffline"]), Convert.ToBoolean(x["IsReadOnly"])))
            .Where(x => x.Eligible).ToArray();
    }
    public static void Revalidate(DeploymentDisk selected, CancellationToken ct)
    {
        var current = Read(ct).SingleOrDefault(x => x.Number == selected.Number);
        if (current is null || current.UniqueId != selected.UniqueId || current.Size != selected.Size)
            throw new InvalidOperationException("The selected disk changed or is no longer eligible. No further disk operations are allowed.");
    }
    public static bool Uefi()
    {
        if (!GetFirmwareType(out var type) || type is not (FirmwareBios or FirmwareUefi)) throw new InvalidOperationException("Cannot determine the WinPE firmware mode.");
        return type == FirmwareUefi;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}
