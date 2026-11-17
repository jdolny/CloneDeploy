using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Correlates TLS connections with clients and routed requests without logging credentials.</summary>
public static class HttpsConnectionDiagnostics
{
    public const string LogCategory = "CloneDeploy_Server.ConnectionDiagnostics";

    public static void Configure(KestrelServerOptions options)
        => options.ConfigureEndpointDefaults(endpoint => endpoint.Use(next => async connection =>
        {
            var logger = options.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
            logger.LogDebug("Transport connection {ConnectionId} opened from {RemoteEndpoint} to {LocalEndpoint}",
                connection.ConnectionId, connection.RemoteEndPoint, connection.LocalEndPoint);
            try { await next(connection); }
            finally
            {
                logger.LogDebug("Transport connection {ConnectionId} ended from {RemoteEndpoint}",
                    connection.ConnectionId, connection.RemoteEndPoint);
            }
        }));

    public static void UseHttpsConnectionDiagnostics(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        app.Use(async (context, next) =>
        {
            if (context.Request.IsHttps && logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("HTTPS request on connection {ConnectionId} from {RemoteAddress}:{RemotePort}: {Method} {Endpoint}",
                    context.Connection.Id, context.Connection.RemoteIpAddress, context.Connection.RemotePort,
                    context.Request.Method, context.GetEndpoint()?.DisplayName ?? "Unmatched endpoint");
            await next(context);
        });
    }
}
