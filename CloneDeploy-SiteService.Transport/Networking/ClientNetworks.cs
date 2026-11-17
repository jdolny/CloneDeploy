using System.Net;
namespace CloneDeploy.SiteServices;

public static class ClientNetworks
{
    private const int MaximumNetworkListCharacters = 4096;

    private static string[] Entries(string value) => value.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static void Validate(string value)
    {
        if (value is null || value.Length > MaximumNetworkListCharacters) throw new InvalidDataException("Invalid client networks.");
        foreach (var entry in Entries(value))
            if (!IPNetwork.TryParse(entry, out var network) || network.PrefixLength == 0)
                throw new InvalidDataException("Enter explicit CIDR client networks (for example 192.168.10.0/24). Unrestricted networks are not allowed.");
    }
    public static bool Contains(string value, IPAddress? address)
    {
        if (address is null || string.IsNullOrWhiteSpace(value)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return Entries(value).Any(entry => IPNetwork.TryParse(entry, out var network) && network.PrefixLength > 0 && network.Contains(address));
    }
}
