using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace CloneDeploy.SiteServices;

public sealed record SiteBootSettings
{
    public bool PxeEnabled { get; set; }
    public bool TftpEnabled { get; set; }
    public bool ListenDhcp { get; set; } = true;
    public bool ListenProxy { get; set; } = true;
    public string ListenAddress { get; set; } = "";
    public string NextServer { get; set; } = "";
    public void Validate()
    {
        if (!PxeEnabled && !TftpEnabled) return;
        if (!IsAddress(ListenAddress)) throw new InvalidDataException("Enter a specific local IPv4 address for the boot listeners.");
        if (PxeEnabled && !IsAddress(NextServer)) throw new InvalidDataException("Enter the TFTP server IPv4 address.");
        if (PxeEnabled && !ListenDhcp && !ListenProxy) throw new InvalidDataException("Enable at least one ProxyDHCP listener.");
    }
    private static bool IsAddress(string value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
        ip.ToString() == value && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) && ip.GetAddressBytes()[0] < 224;
}
public sealed record SiteBootStatus(int AppliedRevision, bool PxeRunning, bool TftpRunning, string? Error, int ActiveTransfers = 0);
public sealed record SiteBootFile(string Path, long Length, string Sha256);
public sealed record SiteBootReservation(string Mac, int DefinitionId, Guid BuildId)
{
    public string Architecture { get; init; } = "x64";
}
public sealed record SiteBootManifest(SiteBootFile[] Files, SiteBootReservation[] Reservations,
    string BiosBootFile, string Efi32BootFile, string Efi64BootFile)
{
    public string EfiArm64BootFile { get; init; } = "ipxeboot/arm64-sb/ipxe-shim.efi";
    public SiteBootDefault? NativeDefault { get; init; }
    public SiteBootDefault? NativeArm64Default { get; init; }
    public string[] KnownMacs { get; init; } = [];
}
public sealed record SiteBootDefault(int DefinitionId, Guid BuildId);
