using System.Net;

namespace CloneDeploy_Server.Backend.Services;

public static class StorageClientNetworks
{
    public static string Normalize(string? input)
    {
        if (input?.Length > 8192) throw new ArgumentException("Preferred client networks must not exceed 8192 characters.");
        var entries = (input ?? "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length > 128) throw new ArgumentException("Specify at most 128 preferred client networks.");
        var networks = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.Contains('/') || entry.Contains('%') || !IPNetwork.TryParse(entry, out var network) || network.BaseAddress.IsIPv4MappedToIPv6)
                throw new ArgumentException($"Invalid network '{entry}'. Use a network address in CIDR notation, such as 10.20.0.0/16 or 2001:db8::/32.");
            var bytes = network.BaseAddress.GetAddressBytes();
            for (var i = 0; i < bytes.Length; i++)
            {
                var bits = Math.Clamp(network.PrefixLength - i * 8, 0, 8);
                bytes[i] &= (byte)(0xff << (8 - bits));
            }
            networks.Add(new IPNetwork(new IPAddress(bytes), network.PrefixLength).ToString());
        }
        return string.Join('\n', networks.Distinct(StringComparer.Ordinal));
    }

    // No match (including an unknown address) is a fallback, not an access denial.
    public static int Match(string? networks, IPAddress? address)
    {
        if (address is null) return -1;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var result = -1;
        foreach (var entry in (networks ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (IPNetwork.TryParse(entry, out var network) && network.Contains(address))
                result = Math.Max(result, network.PrefixLength);
        return result;
    }
}
