// Ported from ToemsProxyDHCP. See THIRD-PARTY-NOTICES.txt.
#nullable disable
using System.Net;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reply options selected by the DHCP and proxy handlers.</summary>
public class DHCPReplyOptions
{
    public IPAddress NextServer = null;
    public string BootFileName = null;
    public string Reason = "Discovery response; bootloader selection follows on UDP 4011.";
    public Dictionary<DHCPOption, byte[]> OtherOptions = new();
}

public class DHCPReply
{
    private readonly DHCPRequest _dhcpRequest;
    private const int PORT_TO_SEND_TO_CLIENT = 68;
    private const int PORT_TO_SEND_TO_RELAY = 67;

    public DHCPReply(DHCPRequest request) => _dhcpRequest = request;

    /// <summary>Builds the original reply in one buffer; the worker sends it asynchronously.</summary>
    public PxeReplyDto CreateReply(DHCPMsgType msgType, DHCPReplyOptions replyData, int replyPort)
    {
        var packet = _dhcpRequest.requestData;
        var configuration = _dhcpRequest.Configuration;
        var relayInfo = OptionData.GetOptionData(DHCPOption.RelayInfo, packet);
        // Fixed BOOTP header, message type, server identifier, other options, and end marker.
        var length = 240 + 3 + 6 + 1;
        foreach (var option in replyData.OtherOptions.Values) length += 2 + option.Length;
        if (relayInfo is not null) length += 2 + relayInfo.Length;
        var data = new byte[length];
        WriteHeader(data, packet);

        "ToemsProxy"u8.CopyTo(data.AsSpan(44));
        data[44 + "ToemsProxy"u8.Length] = 0;
        if (replyData.NextServer is not null)
        {
            if (replyData.NextServer.Equals(configuration.NextServer))
                configuration.NextServerBytes.CopyTo(data, 20);
            else
                replyData.NextServer.TryWriteBytes(data.AsSpan(20, 4), out _);
        }
        if (replyData.BootFileName is not null)
        {
            var file = configuration.GetBootFileBytes(replyData.BootFileName);
            var count = Math.Min(file.Length, 127);
            file[..count].CopyTo(data.AsSpan(108));
            data[108 + count] = 0;
        }

        var offset = 240;
        data[offset++] = (byte)DHCPOption.DHCPMessageTYPE;
        data[offset++] = 1;
        data[offset++] = (byte)msgType;
        WriteOption(data, ref offset, DHCPOption.ServerIdentifier, configuration.ServerIdentifierBytes);
        foreach (var option in replyData.OtherOptions)
            WriteOption(data, ref offset, option.Key, option.Value);
        if (relayInfo is not null) WriteOption(data, ref offset, DHCPOption.RelayInfo, relayInfo);
        data[offset] = 255;

        IPEndPoint destination;
        if (msgType == DHCPMsgType.DHCPACK)
            destination = new IPEndPoint(new IPAddress(packet.ciaddr), replyPort);
        else if (packet.giaddr[0] == 0 && packet.giaddr[1] == 0 && packet.giaddr[2] == 0 && packet.giaddr[3] == 0)
            destination = new IPEndPoint(IPAddress.Broadcast, PORT_TO_SEND_TO_CLIENT);
        else
            destination = new IPEndPoint(new IPAddress(packet.giaddr), PORT_TO_SEND_TO_RELAY);
        return new PxeReplyDto(data, destination) { BootFile = replyData.BootFileName, Architecture = _dhcpRequest.GetClientSystemArch().ToString(), Reason = replyData.Reason };
    }

    private static void WriteHeader(byte[] data, DHCPMessage.Packet packet)
    {
        data[0] = 2;
        data[1] = packet.htype;
        data[2] = packet.hlen;
        data[3] = packet.hops;
        packet.xid.CopyTo(data, 4);
        packet.secs.CopyTo(data, 8);
        packet.flags.CopyTo(data, 10);
        packet.ciaddr.CopyTo(data, 12);
        packet.yiaddr.CopyTo(data, 16);
        packet.siaddr.CopyTo(data, 20);
        packet.giaddr.CopyTo(data, 24);
        packet.chaddr.CopyTo(data, 28);
        packet.sname.CopyTo(data, 44);
        packet.file.CopyTo(data, 108);
        packet.mcookie.CopyTo(data, 236);
    }

    private static void WriteOption(byte[] data, ref int offset, DHCPOption option, byte[] value)
    {
        data[offset++] = (byte)option;
        data[offset++] = checked((byte)value.Length);
        value.CopyTo(data, offset);
        offset += value.Length;
    }
}
