using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CloneDeploy.SiteServices;

public sealed class TrustedSiteProxyOptions
{
    // Exact leaf certificate pins, not CA pins. Removing a pin revokes the proxy for subsequent requests.
    public string[] CertificateSha256 { get; set; } = [];
}

public static class TrustedSiteProxy
{
    private const int MaximumEncodedCertificateCharacters = 16 * 1024;

    private sealed record ForwardedIdentity(X509Certificate2? Certificate);
    private sealed record StorageProxy(string Fingerprint);
    public static string? StorageFingerprint(HttpContext context) => context.Features.Get<StorageProxy>()?.Fingerprint;
    public static bool IsProxied(HttpContext context) => context.Features.Get<ForwardedIdentity>() is not null;

    public static void AddTrustedSiteProxies(this IServiceCollection services) => services
        .AddOptions<TrustedSiteProxyOptions>().BindConfiguration("SiteProxies")
        .Validate(options => options.CertificateSha256.All(SiteProxyProtocol.IsFingerprint),
            "SiteProxies:CertificateSha256 entries must be 64 hexadecimal characters.").ValidateOnStart();

    public static Task<X509Certificate2?> GetAgentCertificateAsync(HttpContext context) =>
        context.Features.Get<ForwardedIdentity>() is { } identity
            ? Task.FromResult(identity.Certificate)
            : context.Connection.GetClientCertificateAsync(context.RequestAborted);

    public static IApplicationBuilder UseTrustedSiteProxies(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Request.Headers;
        var hasEnvelope = headers.ContainsKey(SiteProxyProtocol.VersionHeader) ||
            headers.ContainsKey(SiteProxyProtocol.CertificateHeader) || headers.ContainsKey(SiteProxyProtocol.AddressHeader);
        // The management API authenticates the service's own TLS identity, not a forwarded agent.
        if (!hasEnvelope && context.Request.Path.StartsWithSegments("/api/site-services"))
        { await next(context); return; }
        var tls = context.Request.IsHttps ? await context.Connection.GetClientCertificateAsync(context.RequestAborted) : null;
        var pins = context.RequestServices.GetRequiredService<IOptionsMonitor<TrustedSiteProxyOptions>>().CurrentValue.CertificateSha256;
        var trusted = tls is not null && pins.Contains(tls.GetCertHashString(HashAlgorithmName.SHA256), StringComparer.OrdinalIgnoreCase);
        if (tls is not null && context.RequestServices.GetService<ISiteProxyTrust>() is { } registry)
            trusted = await registry.IsTrustedAsync(tls, context.RequestAborted) ?? trusted;
        if (!hasEnvelope && !trusted)
        {
            await next(context);
            return;
        }

        // TLS proves possession of the configured proxy key. Never trust a header or source IP alone.
        if (!trusted || !SiteProxyProtocol.IsCurrentClientCertificate(tls!, DateTimeOffset.UtcNow) ||
            !(SiteProxyProtocol.Allows(context.Request.Path) || ListenerPorts.PortalRoute(context.Request.Path, context.Request.Method)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (headers[SiteProxyProtocol.VersionHeader].Count != 1 || headers[SiteProxyProtocol.VersionHeader] != SiteProxyProtocol.Version ||
            headers[SiteProxyProtocol.AddressHeader].Count != 1 || !IPAddress.TryParse(headers[SiteProxyProtocol.AddressHeader], out var address) ||
            headers[SiteProxyProtocol.CertificateHeader].Count > 1)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (context.RequestServices.GetService<ISiteProxyTrust>() is ISiteProxyAccessPolicy policy &&
            !await policy.AllowsAsync(tls!, context.Request.Path, address, context.RequestAborted))
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        X509Certificate2? certificate = null;
        try
        {
            if (headers.ContainsKey(SiteProxyProtocol.CertificateHeader))
            {
                var encoded = headers[SiteProxyProtocol.CertificateHeader].ToString();
                if (encoded.Length is 0 or > MaximumEncodedCertificateCharacters) throw new FormatException();
                certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using (certificate)
        {
            // Request-local feature: never replace the shared backend TLS connection's certificate.
            context.Features.Set(new ForwardedIdentity(certificate));
            if (headers[SiteStorageProtocol.CapabilityHeader] == "1" && !headers.ContainsKey(SiteStorageProtocol.BypassHeader))
                context.Features.Set(new StorageProxy(tls!.GetCertHashString(HashAlgorithmName.SHA256)));
            var originalAddress = context.Connection.RemoteIpAddress;
            var originalPort = context.Connection.RemotePort;
            context.Connection.RemoteIpAddress = address;
            context.Connection.RemotePort = 0;
            foreach (var name in headers.Keys.Where(SiteProxyProtocol.IsIdentityHeader).ToArray()) headers.Remove(name);
            try { await next(context); }
            finally
            {
                context.Connection.RemoteIpAddress = originalAddress;
                context.Connection.RemotePort = originalPort;
                context.Features.Set<ForwardedIdentity>(null);
                context.Features.Set<StorageProxy>(null);
            }
        }
    });
}
