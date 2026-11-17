using System.Net;
using System.Net.Sockets;

namespace CloneDeploy_Agent.Contracts;

public sealed record AgentWakeRequest(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc, string Subnet, string[] Macs);
public sealed record AgentWakeReply(Guid RequestId, string[] SentMacs, string Detail);

public static class WakeNetwork
{
    private const int MacAddressBytes = 6;
    private const int MagicPacketRepetitions = 16;
    private const int MagicPacketHeaderBytes = 6;
    private const byte MagicPacketHeaderValue = 255;

    // IPv4 broadcast-capable networks only. /31 and /32 have no broadcast hosts.
    public static bool TryParse(string? value, out string subnet, out IPAddress address, out IPAddress broadcast)
    {
        subnet = ""; address = broadcast = IPAddress.None;
        var parts = value?.Split('/');
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 30) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] is 0 or 127 or >= 224 || (bytes[0] == 169 && bytes[1] == 254)) return false;
        var number = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var mask = uint.MaxValue << (32 - prefix);
        var network = number & mask;
        var last = network | ~mask;
        IPAddress Ip(uint n) => new(new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n });
        subnet = $"{Ip(network)}/{prefix}"; address = ip; broadcast = Ip(last);
        return true;
    }

    public static IEnumerable<string> Addresses(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static string? Mac(string? value)
    {
        var mac = value?.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpperInvariant();
        return mac is { Length: 12 } && mac.All(Uri.IsHexDigit) && mac != "000000000000" && (Convert.ToByte(mac[..2], 16) & 1) == 0 ? mac : null;
    }
    public static byte[] Packet(string mac)
    {
        var bytes = Convert.FromHexString(Mac(mac) ?? throw new ArgumentException("Invalid wake-up MAC address."));
        var packet = new byte[MagicPacketHeaderBytes + MagicPacketRepetitions * MacAddressBytes];
        Array.Fill(packet, MagicPacketHeaderValue, 0, MagicPacketHeaderBytes);
        for (var i = 0; i < MagicPacketRepetitions; i++) bytes.CopyTo(packet, MagicPacketHeaderBytes + i * MacAddressBytes);
        return packet;
    }
}
