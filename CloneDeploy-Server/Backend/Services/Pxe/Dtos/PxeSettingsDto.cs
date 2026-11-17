using CloneDeploy_Server.Localization;
using System.Net;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Stores the listener addresses and boot filenames used to answer PXE clients.
/// </summary>
public sealed record PxeSettingsDto
{
    public bool Enabled { get; set; }
    public string Interface { get; set; } = "0.0.0.0";
    public string NextServer { get; set; } = "";
    public string ServerIdentifierOverride { get; set; } = "";
    public string BiosBootFile { get; set; } = "ipxeboot/undionly.kpxe";
    public string Efi32BootFile { get; set; } = "ipxeboot/i386/ipxe.efi";
    public string Efi64BootFile { get; set; } = "ipxeboot/x86_64-sb/ipxe-shim.efi";

    public string EfiArm64BootFile { get; set; } = "ipxeboot/arm64-sb/ipxe-shim.efi";

    public string? Validate()
    {
        if (!IsIpv4(Interface, true)) return ErrorKeys.Pxe.InvalidInterface;
        if ((Enabled || !string.IsNullOrEmpty(NextServer)) && !IsIpv4(NextServer))
            return ErrorKeys.Pxe.InvalidNextServer;
        if (!string.IsNullOrEmpty(ServerIdentifierOverride) && !IsIpv4(ServerIdentifierOverride))
            return ErrorKeys.Pxe.InvalidServerIdentifier;
        if (Enabled && Interface == "0.0.0.0" && string.IsNullOrEmpty(ServerIdentifierOverride))
            return ErrorKeys.Pxe.ServerIdentifierRequired;
        foreach (var file in new[] { BiosBootFile, Efi32BootFile, Efi64BootFile, EfiArm64BootFile })
            if (string.IsNullOrWhiteSpace(file) || file.Length > 127 || file.Any(c => c < 32 || c > 126) ||
                file.StartsWith('/') || file.Contains('\\') || file.Contains(':') || file.Split('/').Any(x => x is ".." or "." or ""))
                return ErrorKeys.Pxe.InvalidBootFile;
        return null;
    }

    private static bool IsIpv4(string? value, bool allowAny = false) =>
        value is not null && value.Split('.').Length == 4 &&
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
        ip.ToString() == value && (allowAny && ip.Equals(IPAddress.Any) ||
        !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) && ip.GetAddressBytes()[0] < 224);
}

