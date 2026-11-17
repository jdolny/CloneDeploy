namespace CloneDeploy_Server.Backend.Services;

/// <summary>Connects the advertised URL to certificate names without issuing or replacing certificates.</summary>
public static class WebCertificateAddresses
{
    public static string? PublicHost(string? url)
    {
        try
        {
            var normalized = ServerUrlSettingsStore.Normalize(url);
            if (normalized is null) return null;
            var host = new Uri(normalized).IdnHost.Trim('[', ']');
            return System.Net.IPAddress.TryParse(host, out var ip) ? ip.ToString() : host;
        }
        catch (InvalidDataException) { return null; }
    }

    public static bool HostChanged(string? previousUrl, string? nextUrl) =>
        PublicHost(previousUrl) is { } previous && PublicHost(nextUrl) is { } next &&
        !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase);

    public static string[] SuggestedHosts(string? publicUrl, IEnumerable<string> existing) =>
        (PublicHost(publicUrl) is { } host ? new[] { host }.Concat(existing) : existing)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
