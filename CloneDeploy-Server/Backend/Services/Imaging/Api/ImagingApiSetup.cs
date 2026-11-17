using System.Threading.RateLimiting;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Sets up sign-in and access limits for imaging requests.
/// </summary>
public static class ImagingApiSetup
{
    public static void AddImagingApi(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<TftpRootDirectory>();
        services.AddSingleton<ImagerSessions>();
        services.TryAddScoped<StorageReadSelection>(sp => new(sp.GetRequiredService<CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory>(),
            sp.GetRequiredService<StorageContentStorage>(), sp.GetRequiredService<TimeProvider>()) { Requests = sp.GetRequiredService<IHttpContextAccessor>() });
        services.AddOptions<ImagingApiOptions>().BindConfiguration("ImagingApi");
        services.AddSingleton<ImagingTokens>();
        services.AddScoped<ImagingAccess>();
        services.AddScoped<ImagingBootService>(sp => new(sp.GetRequiredService<CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory>(),
            sp.GetRequiredService<WinPeArtifactStorage>(), sp.GetRequiredService<TftpRootDirectory>(), sp.GetRequiredService<ILogger<ImagingBootService>>()) { Shared = sp.GetService<SharedContentDelivery>() });
        services.AddScoped<ImagingLoginService>();
        services.AddScoped<ImagingUploadService>();
        services.AddScoped<PreparedCaptureService>();
        services.AddScoped<ImagingDeviceService>();
        services.AddScoped<ImagingTaskService>();
        services.AddScoped<MulticastQueue>();
        services.AddScoped<ImagingComputerAccountLookup>();
        services.AddHostedService<MulticastSenderWorker>();
        services.AddScoped<ImagingDeploymentClaims>(sp => new(sp.GetRequiredService<CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), sp.GetRequiredService<StorageReadSelection>(), sp.GetRequiredService<IHttpContextAccessor>())
            { Shared = sp.GetService<SharedContentDelivery>(), Site = sp.GetService<SiteDownloadDelivery>(),
                ComputerAccounts = sp.GetRequiredService<ImagingComputerAccountLookup>() });
        services.AddHostedService<ImageUploadCleanupWorker>();
        services.AddScoped<ImageReplicationAdministration>();
        services.AddScoped<IImageReplicaFiles>(sp => new ImageReplicaFiles(sp.GetRequiredService<StorageContentStorage>())
            { Transfers = sp.GetRequiredService<ReplicationTransfers>() });
        services.AddScoped<ImageReplicationRunner>();
        services.AddHostedService<ImageReplicationWorker>();
        services.AddScoped<ImageContentCleanup>();
        services.AddHostedService<ImageContentCleanupWorker>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ImagingAuthentication>(ImagingAuthentication.SchemeName, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(ImagingAuthentication.PolicyName, policy =>
            policy.AddAuthenticationSchemes(ImagingAuthentication.SchemeName).RequireAuthenticatedUser().RequireClaim("imaging_kind", "user", "automation"));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("ImagerDownload", context => RateLimitPartition.GetFixedWindowLimiter(
                (context.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true
                    ? context.Connection.RemoteIpAddress.MapToIPv4() : context.Connection.RemoteIpAddress)?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/imaging/client")
                    ? RateLimitPartition.GetConcurrencyLimiter("imager-downloads", _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
            options.AddPolicy("ImagingDeploymentClaim", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("ImagingLogin", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("ImagingBoot", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
