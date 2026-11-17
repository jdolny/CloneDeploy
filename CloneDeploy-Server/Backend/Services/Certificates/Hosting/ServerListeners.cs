using System.Net;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy.SiteServices;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace CloneDeploy_Server.Backend.Services;

public static class ServerListeners
{
    public static void AddServerListeners(this WebApplicationBuilder builder)
    {
        var ports = ListenerPorts.Read(builder.Configuration);
        ClientNetworks.Validate(builder.Configuration["Server:AgentAdmin:AllowedNetworks"] ?? "");
        if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidDataException("Configure Server:Listeners instead of Kestrel:Endpoints. Shared legacy listeners are not supported.");
        builder.Services.AddSingleton(ports);
        builder.WebHost.ConfigureKestrel(server =>
        {
            var address = IPAddress.Parse(ports.ListenAddress);
            server.Listen(address, ports.HttpPort);
            if (!ports.Enabled) return;
            foreach (var port in new[] { ports.ManagementPort, ports.ServicePort, ports.PortalPort })
                server.Listen(address, port, endpoint => endpoint.UseHttps(https =>
                {
                    https.ClientCertificateMode = port == ports.ServicePort ? ClientCertificateMode.AllowCertificate : ClientCertificateMode.NoCertificate;
                    if (port == ports.ServicePort)
                    {
                        server.ApplicationServices.GetRequiredService<WebCertificateRuntime>().ConfigureService(https);
                        https.AllowAnyClientCertificate();
                    }
                }));
        });
    }
    public static void UseServerListeners(this WebApplication app)
    {
        var ports = app.Services.GetRequiredService<ListenerPorts>();
        app.Use(async (context, next) =>
        {
            if (!ports.Allows(context, TrustedSiteProxy.IsProxied(context))) { context.Response.StatusCode = 404; return; }
            // Protect the entire management surface, including login/SSO, setup, UI APIs and Blazor.
            // Never use caller-supplied forwarding headers to decide whether a browser is private.
            var management = context.Connection.LocalPort == ports.ManagementPort ||
                context.Connection.LocalPort == ports.HttpPort && !context.Request.Path.StartsWithSegments("/api/boot");
            if (management && !PrivateManagementAccess.Allows(context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!TrustedSiteProxy.IsProxied(context) && (context.Request.Path.StartsWithSegments("/signin-entra-agent-admin") || context.Request.Path.StartsWithSegments("/agent/admin-sign-in") || context.Request.Path.StartsWithSegments("/api/agent/v1/admin")) &&
                !ClientNetworks.Contains(app.Configuration["Server:AgentAdmin:AllowedNetworks"] ?? "", context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = 404; return; }
            await next(context);
        });
    }
}
