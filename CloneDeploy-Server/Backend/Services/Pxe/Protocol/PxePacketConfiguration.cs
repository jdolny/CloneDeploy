using System.Net;
using System.Text;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Prepares a configuration snapshot once for the lifetime of the listeners.</summary>
public sealed class PxePacketConfiguration
{
    internal PxeSettingsDto Settings { get; }
    public NativePxeReservations? Reservations { get; init; }
    internal IPAddress ServerIdentifier { get; }
    internal IPAddress NextServer { get; }
    internal byte[] ServerIdentifierBytes { get; }
    internal byte[] NextServerBytes { get; }
    internal static readonly byte[] VendorClass = "PXEClient"u8.ToArray();
    internal static readonly byte[] BcdX86 = "\\boot\\BCDx86"u8.ToArray();
    internal static readonly byte[] BcdX64 = "\\boot\\BCDx64"u8.ToArray();
    private readonly byte[] _biosBootFile;
    private readonly byte[] _efi32BootFile;
    private readonly byte[] _efi64BootFile;
    private readonly byte[] _efiArm64BootFile;

    public PxePacketConfiguration(PxeSettingsDto settings)
    {
        Settings = settings with { };
        ServerIdentifier = IPAddress.Parse(string.IsNullOrEmpty(Settings.ServerIdentifierOverride)
            ? Settings.Interface : Settings.ServerIdentifierOverride);
        NextServer = IPAddress.Parse(Settings.NextServer);
        ServerIdentifierBytes = ServerIdentifier.GetAddressBytes();
        NextServerBytes = NextServer.GetAddressBytes();
        _biosBootFile = Encoding.ASCII.GetBytes(Settings.BiosBootFile);
        _efi32BootFile = Encoding.ASCII.GetBytes(Settings.Efi32BootFile);
        _efi64BootFile = Encoding.ASCII.GetBytes(Settings.Efi64BootFile);
        _efiArm64BootFile = Encoding.ASCII.GetBytes(Settings.EfiArm64BootFile);
    }

    internal ReadOnlySpan<byte> GetBootFileBytes(string file) =>
        file == Settings.BiosBootFile ? _biosBootFile :
        file == Settings.Efi32BootFile ? _efi32BootFile :
        file == Settings.Efi64BootFile ? _efi64BootFile :
        file == Settings.EfiArm64BootFile ? _efiArm64BootFile : Encoding.ASCII.GetBytes(file);
}
