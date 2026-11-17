using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace CloneDeploy.SiteServices;

public sealed class ListenerPorts
{
    public const int DefaultManagementPort = 443;
    public const int DefaultServicePort = 8443;
    public const int DefaultPortalPort = 8444;
    public const int DefaultHttpPort = 80;
    public const int MaximumPort = 65_535;

    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ManagementPort { get; set; } = DefaultManagementPort;
    public int ServicePort { get; set; } = DefaultServicePort;
    public int PortalPort { get; set; } = DefaultPortalPort;
    public int HttpPort { get; set; } = DefaultHttpPort;
    public static ListenerPorts Read(IConfiguration configuration)
    {
        var ports = new ListenerPorts();
        configuration.GetSection("Server:Listeners").Bind(ports);
        ports.Validate();
        return ports;
    }
    public void Validate()
    {
        var ports = new[] { ManagementPort, ServicePort, PortalPort, HttpPort };
        if (ports.Any(x => x is < 1 or > MaximumPort) || ports.Distinct().Count() != ports.Length)
            throw new InvalidDataException("Management, service, portal and HTTP ports must be distinct and between 1 and 65535.");
        if (!System.Net.IPAddress.TryParse(ListenAddress, out _)) throw new InvalidDataException("Listener address must be an IP address.");
    }
    public static bool AdminRoute(PathString path, string method)
    {
        var parts = path.Value?.TrimEnd('/').Split('/');
        if (path.Equals(new PathString("/signin-entra-agent-admin"))) return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);
        return parts is { Length: 4 or 5 } && parts[1].Equals("agent", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("admin-sign-in", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[3], out _) &&
            (parts.Length == 4 ? HttpMethods.IsGet(method) || HttpMethods.IsPost(method) : parts[4].Equals("entra", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method));
    }
    public static bool PortalRoute(PathString path, string method)
    {
        if (AdminRoute(path, method)) return true;
        if (path.Equals(new PathString("/signin-entra-portal"))) return HttpMethods.IsPost(method) || HttpMethods.IsGet(method);
        var parts = path.Value?.TrimEnd('/').Split('/');
        return parts is { Length: 4 or 5 } && parts[1].Equals("agent", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("portal-sign-in", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[3], out _) &&
            (parts.Length == 4 ? HttpMethods.IsGet(method) || HttpMethods.IsPost(method) :
                parts[4].Equals("entra", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method));
    }
    public static bool ClientPath(PathString path) => SiteProxyProtocol.Allows(path) || path.StartsWithSegments("/api/site-services");
    public bool Allows(HttpContext context, bool proxied)
    {
        var port = context.Connection.LocalPort;
        var path = context.Request.Path;
        var portal = PortalRoute(path, context.Request.Method);
        if (port == PortalPort) return Enabled && context.Request.IsHttps && portal;
        if (port == ServicePort) return Enabled && context.Request.IsHttps && (ClientPath(path) || portal && proxied);
        if (port == ManagementPort) return Enabled && context.Request.IsHttps && !ClientPath(path) && !path.StartsWithSegments("/signin-entra-agent-admin") && !path.StartsWithSegments("/agent/admin-sign-in") && !path.StartsWithSegments("/agent/portal-sign-in") && !path.StartsWithSegments("/signin-entra-portal");
        if (port == HttpPort && !context.Request.IsHttps)
            return path.StartsWithSegments("/api/boot") || !Enabled && !ClientPath(path) && !path.StartsWithSegments("/signin-entra-agent-admin") && !path.StartsWithSegments("/agent/admin-sign-in") && !path.StartsWithSegments("/agent/portal-sign-in") && !path.StartsWithSegments("/signin-entra-portal");
        return false;
    }
}
