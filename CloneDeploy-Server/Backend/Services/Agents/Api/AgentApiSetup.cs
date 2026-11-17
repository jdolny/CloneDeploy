using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Files;
﻿using System.Threading.RateLimiting;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Sets up secure communication between device agents and the server.
/// </summary>
public static class AgentApiSetup
{
    public static void MapAgentHub(this WebApplication app) => app.MapHub<AgentHub>(AgentHubProtocol.Route);
    public static void UseAgentApiTransport(this WebApplication app) => app.Use(async (context, next) =>
    {
        if ((context.Request.Path.StartsWithSegments("/api/agent") || context.Request.Path.StartsWithSegments("/api/site-services")) && !context.Request.IsHttps)
        {
            await Results.Problem(statusCode: 400, detail: "Agent API requests require HTTPS.").ExecuteAsync(context);
            return;
        }
        await next(context);
    });

    public static void AddAgentApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ServerMessageSigner>();
        var trust = new AgentCertificateTrust();
        builder.Services.AddSingleton(trust);
        builder.Services.AddSingleton<AgentTrustRefresh>();
        builder.Services.AddScoped<AgentEnrollmentService>();
        builder.Services.AddHostedService<AgentEnrollmentRequestCleanup>();
        builder.Services.AddScoped<MacAgentReleaseService>();
        builder.Services.AddSingleton<AgentEnrollmentRateSettings>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentEnrollmentRateSettings>());
        builder.Services.AddSingleton<AgentAdminSessions>();
        builder.Services.AddScoped<AgentAdminBrowserLogin>();
        builder.Services.AddSingleton<AgentPortalSessions>();
        builder.Services.AddAntiforgery();
        builder.Services.AddScoped<IAdPasswordValidator, AdPasswordValidator>();
        builder.Services.AddScoped<AgentInstallerService>();
        builder.Services.AddScoped<AgentReleaseService>();
        builder.Services.AddScoped<AgentCertificateValidationService>();
        builder.Services.AddScoped<AgentCheckinService>();
        builder.Services.AddScoped<AgentPolicyExecutionService>(sp => new(sp.GetRequiredService<ICloneDeployDbFactory>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<StoredFileStorage>(), sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<ILogger<AgentPolicyExecutionService>>(), sp.GetService<StorageReadSelection>(), sp.GetService<IHttpContextAccessor>()) { Shared = sp.GetService<SharedContentDelivery>(), SiteRequests = sp.GetService<IHttpContextAccessor>() });
        builder.Services.AddScoped<AgentTaskExecutionService>(sp => new(sp.GetRequiredService<ICloneDeployDbFactory>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<StoredFileStorage>(), sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<DeviceTaskQueue>(), sp.GetRequiredService<ILogger<AgentTaskExecutionService>>()) { Shared = sp.GetService<SharedContentDelivery>(), SiteRequests = sp.GetService<IHttpContextAccessor>() });
        builder.Services.AddSignalR().AddHubOptions<AgentHub>(options =>
        {
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.EnableDetailedErrors = false;
        });
        builder.Services.AddSingleton<HelpPresenceService>();
        builder.Services.AddHostedService<HelpUpdateNotifications>();
        builder.Services.AddSingleton<AgentServerInstance>();
        builder.Services.AddScoped<AgentConnectionService>();
        builder.Services.AddScoped<AgentActivityService>();
        builder.Services.AddSingleton<DeviceActivityFeed>();
        builder.Services.AddHostedService<DeviceActivityNotifications>();
        builder.Services.AddScoped<IDeviceTaskNotifier, AgentDeviceTaskNotifier>();
        builder.Services.AddScoped<AgentLogService>();
        builder.Services.AddScoped<AgentDeviceToolService>();
        builder.Services.AddScoped<AgentWakeService>();
        builder.Services.AddScoped<DeviceRenameService>();
        builder.Services.AddSingleton<RemoteDesktopSessions>();
        builder.Services.AddScoped<RemoteDesktopService>();
        builder.Services.AddScoped<RemoteAccessRules>();
        builder.Services.AddSingleton<RemoteTerminalSessions>();
        builder.Services.AddScoped<RemoteTerminalService>();
        builder.Services.AddScoped<AgentCheckinCommandService>();
        builder.Services.AddScoped<AgentInventoryService>();
        builder.Services.AddScoped<AgentInventoryCommandService>();
        builder.Services.AddHostedService<AgentConnectionCleanup>();
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentCertificateAuthentication>(AgentCertificateAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorizationBuilder().AddPolicy(AgentCertificateAuthentication.PolicyName, policy =>
            policy.AddAuthenticationSchemes(AgentCertificateAuthentication.SchemeName).RequireAuthenticatedUser()
                .RequireClaim(AgentCertificateAuthentication.DeviceIdClaim));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("PortalBrowser", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("PortalPassword", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy<int, AgentEnrollmentRatePolicy>("AgentEnrollment");
        });
        builder.Services.AddSingleton<WebCertificateRuntime>();
        builder.WebHost.ConfigureKestrel(options =>
        {
            HttpsConnectionDiagnostics.Configure(options);
            options.ConfigureHttpsDefaults(https =>
            {
                options.ApplicationServices.GetRequiredService<WebCertificateRuntime>().Configure(https);
                // Allow more time for TLS after agent restarts: server logs have shown the
                // default 10-second deadline ending handshakes before check-in routing.
                https.HandshakeTimeout = TimeSpan.FromSeconds(30);
                https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
            });
        });
    }
}
