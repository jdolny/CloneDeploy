using System.Net;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Auth;

public static class PrivateManagementAccess
{
    public static bool Allows(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168,
            AddressFamily.InterNetworkV6 => (bytes[0] & 0xfe) == 0xfc,
            _ => false
        };
    }
}
