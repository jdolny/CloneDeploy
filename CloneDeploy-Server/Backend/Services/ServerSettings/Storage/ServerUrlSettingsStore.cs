using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>The advertised application endpoint. Listener bindings and agent endpoints are separate concerns.</summary>
public sealed class ServerUrlSettingsStore(ICloneDeployDbFactory factory, IConfiguration configuration)
{
    public const string PublicUrlKey = "Server.PublicUrl";
    public const string BootUrlKey = "Server.BootUrl";
    public const string MainServerAddressKey = "Server.MainServerAddress";

    public async Task<string?> ReadMainServerAddressAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var value = await db.AppSettings.Where(x => x.Name == MainServerAddressKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return NormalizeMainServerAddress(value);
    }

    public async Task<string> RequireMainServerAddressAsync(CancellationToken ct = default) =>
        await ReadMainServerAddressAsync(ct) ?? throw new InvalidDataException("Set the main server IP in Administration → Server Settings before creating a Site Service or Windows Worker.");

    public static string? NormalizeMainServerAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!System.Net.IPAddress.TryParse(value.Trim(), out var address) ||
            System.Net.IPAddress.Any.Equals(address) || System.Net.IPAddress.IPv6Any.Equals(address))
            throw new InvalidDataException("Enter a specific main server IPv4 or IPv6 address without a port or URL.");
        return address.ToString();
    }

    public async Task<string?> ReadBootOverrideAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var value = await db.AppSettings.Where(x => x.Name == BootUrlKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return NormalizeBootUrl(value ?? configuration["Server:BootUrl"]);
    }

    public async Task<string?> ReadBootUrlAsync(CancellationToken ct = default) =>
        await ReadBootOverrideAsync(ct) ?? DeriveBootUrl(await ReadAsync(ct));

    public static string? DeriveBootUrl(string? publicUrl)
    {
        var normalized = Normalize(publicUrl);
        return normalized is null ? null : new UriBuilder(normalized) { Scheme = "http", Port = 80 }.Uri.AbsoluteUri;
    }

    public async Task<string> RequireBootUrlAsync(CancellationToken ct = default) =>
        await ReadBootUrlAsync(ct) ?? throw new InvalidDataException("Configure the PXE boot URL or public application URL in Server Settings first.");

    public static string? NormalizeBootUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 2048 || value.Any(c => char.IsControl(c) || c is '$' or '\"' or '\'') ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Use an HTTP or HTTPS application base URL without credentials, query strings, fragments or script characters.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        var value = await db.AppSettings.Where(x => x.Name == PublicUrlKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        // The advertised management origin can differ from the internal listener behind a reverse proxy.
        return Normalize(value ?? configuration["Server:PublicUrl"]);
    }

    public async Task<string> RequireAsync(CancellationToken ct = default) =>
        await ReadAsync(ct) ?? throw new InvalidDataException("Configure the public application URL in Administration → Server Settings first.");

    public async Task<string?> ReadServiceUrlAsync(CancellationToken ct = default) =>
        WithPort(await ReadAsync(ct), CloneDeploy.SiteServices.ListenerPorts.Read(configuration).ServicePort);
    public async Task<string> RequireServiceUrlAsync(CancellationToken ct = default) =>
        await ReadServiceUrlAsync(ct) ?? throw new InvalidDataException("Configure the public application URL first.");
    public async Task<string> RequirePortalUrlAsync(CancellationToken ct = default) =>
        WithPort(await RequireAsync(ct), CloneDeploy.SiteServices.ListenerPorts.Read(configuration).PortalPort)!;
    public static string? WithPort(string? url, int port) => url is null ? null : new UriBuilder(url) { Port = port }.Uri.AbsoluteUri;

    public string? NormalizeManagementUrl(string? value) => Normalize(value);

    public static string? Normalize(string? value)
    {
        if (value is { Length: > 2048 } || value?.Any(c => char.IsControl(c) || c is '$' or '\"' or '\'') == true)
            throw new InvalidDataException("Use an HTTPS application URL of up to 2,048 characters without script characters.");
        return ImagerBundle.NormalizeServerUrl(value);
    }
}
