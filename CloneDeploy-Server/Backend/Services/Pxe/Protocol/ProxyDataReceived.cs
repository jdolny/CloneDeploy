using System.Net;
using System.Text;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Preserves the legacy PXE boot request handling on UDP 4011.</summary>
public sealed class ProxyDataReceived
{
    public PxeReplyDto? Process(DHCPRequest dhcpRequest)
    {
        var requestType = dhcpRequest.GetMsgType();
        var vendorId = dhcpRequest.GetVendorOptionData();
        if (requestType != DHCPMsgType.DHCPREQUEST || vendorId is null ||
            !vendorId.AsSpan().StartsWith("PXEClient"u8))
            return null;

        return ProcessProxyRequest(dhcpRequest);
    }

    private static PxeReplyDto? ProcessProxyRequest(DHCPRequest dhcpRequest)
    {
        // Restore the original repository's default (non-reservation, non-cluster) path.
        var settings = dhcpRequest.Settings;
        var replyOptions = new DHCPReplyOptions
        {
            NextServer = dhcpRequest.Configuration.NextServer,
            Reason = "Configured fallback: no ready native selection for this client/architecture (missing environment, selection conflict, or native PXE disabled)."
        };
        switch (dhcpRequest.GetClientSystemArch())
        {
            case DHCPRequest.ClientSystemArch.Intelx86PC:
                replyOptions.BootFileName = settings.BiosBootFile;
                replyOptions.OtherOptions.Add(DHCPOption.Wpad, PxePacketConfiguration.BcdX86);
                break;
            case DHCPRequest.ClientSystemArch.EFIIA32:
                replyOptions.BootFileName = settings.Efi32BootFile;
                replyOptions.OtherOptions.Add(DHCPOption.Wpad, PxePacketConfiguration.BcdX86);
                break;
            case DHCPRequest.ClientSystemArch.UefiX64:
            case DHCPRequest.ClientSystemArch.EfiByteCode:
                // Retain legacy routing of ID 9 (EFI byte code) to the x64 boot file.
                replyOptions.BootFileName = settings.Efi64BootFile;
                replyOptions.OtherOptions.Add(DHCPOption.Wpad, PxePacketConfiguration.BcdX64);
                break;
            case DHCPRequest.ClientSystemArch.EfiArm64:
                replyOptions.BootFileName = settings.EfiArm64BootFile;
                break;
            default:
                return null;
        }

        var arch = dhcpRequest.GetClientSystemArch();
        if (arch is DHCPRequest.ClientSystemArch.Intelx86PC or DHCPRequest.ClientSystemArch.UefiX64 or DHCPRequest.ClientSystemArch.EfiByteCode or DHCPRequest.ClientSystemArch.EfiArm64)
        {
            var packet = dhcpRequest.requestData;
            var target = arch == DHCPRequest.ClientSystemArch.EfiArm64
                ? dhcpRequest.Configuration.Reservations?.FindArm64(packet.htype, packet.hlen, packet.chaddr)
                : dhcpRequest.Configuration.Reservations?.Find(packet.htype, packet.hlen, packet.chaddr);
            if (dhcpRequest.Configuration.Reservations is { } reservations && packet.chaddr.Length >= 6)
                replyOptions.Reason = reservations.Explain(Convert.ToHexString(packet.chaddr.AsSpan(0, 6)), arch == DHCPRequest.ClientSystemArch.EfiArm64);
            if (target is not null)
            {
                var efi = arch != DHCPRequest.ClientSystemArch.Intelx86PC;
                replyOptions.BootFileName = target.Loader(efi);
                replyOptions.Reason += $" Selected ready native boot environment {target.DefinitionId}, build {target.BuildId}.";
                replyOptions.OtherOptions[DHCPOption.Wpad] = Encoding.ASCII.GetBytes(target.Bcd(efi));
            }
        }

        var replyPort = dhcpRequest.GetSourcePort() == 4011 ? 4011 : 68;
        var reply = new DHCPReply(dhcpRequest);
        return reply.CreateReply(DHCPMsgType.DHCPACK, replyOptions, replyPort);
    }
}
