using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy.SiteServices;
using Yarp.ReverseProxy.Forwarder;

namespace CloneDeploy.SiteService;

public sealed class SiteProxy : IDisposable
{
    private static readonly TimeSpan ForwardingIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackendConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackendConnectionLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpMessageInvoker client;
    private readonly X509Certificate2 identity;
    private readonly X509Certificate2? backendCa;
    private readonly SiteServiceOptions options;
    private readonly AgentCertificateGate agentCertificates;
    private readonly ProxyRequestProtection protection;
    private readonly ForwarderRequestConfig config = new() { ActivityTimeout = ForwardingIdleTimeout };
    public SiteDownloadResponse? Downloads { get; init; }
    public void SetEnrollmentKeyHash(string? hash) => protection.EnrollmentKeys.Configure(hash);

    private volatile SiteProxyAccess access = new();
    public void SetCapabilities(SiteProxyAccess value) { value.Validate(); access = value; }
    private volatile bool imagingEnabled = true;
    public void SetProxyAccess(bool allowImaging, int enrollmentLimit, int loginLimit)
    {
        protection.Configure(enrollmentLimit, loginLimit);
        imagingEnabled = allowImaging;
    }

    public void SetAuthenticationLimit(int limit) => protection.Configure(limit);

    public SiteProxy(SiteServiceOptions options)
    {
        this.options = options;
        SetCapabilities(options.Access);
        agentCertificates = new AgentCertificateGate(options.AgentCaCertificatePath);
        if (options.AuthenticationRequestsPerMinute is < 0 or > SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute)
            throw new InvalidOperationException("AuthenticationRequestsPerMinute must be between 0 and 1000000.");
        protection = new ProxyRequestProtection(options.AuthenticationRequestsPerMinute);
        SetProxyAccess(options.ImagingEnabled, options.AuthenticationRequestsPerMinute, options.ImagingLoginRequestsPerMinute ?? options.AuthenticationRequestsPerMinute);
        SetEnrollmentKeyHash(options.AgentProvisioningKeyHash);
        identity = X509CertificateLoader.LoadPkcs12FromFile(options.IdentityCertificatePath, options.IdentityCertificatePassword,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (!identity.HasPrivateKey || !SiteProxyProtocol.IsCurrentClientCertificate(identity, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("The proxy identity must be a valid client-authentication certificate with a private key.");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, EnableMultipleHttp2Connections = true,
            ConnectTimeout = BackendConnectTimeout, PooledConnectionLifetime = BackendConnectionLifetime,
            SslOptions = new SslClientAuthenticationOptions { ClientCertificates = new X509CertificateCollection { identity } }
        };
        if (!string.IsNullOrEmpty(options.BackendAddress)) BackendConnection.Configure(handler, options.BackendAddress);
        if (!string.IsNullOrWhiteSpace(options.BackendCaCertificatePath))
        {
            backendCa = X509CertificateLoader.LoadCertificateFromFile(options.BackendCaCertificatePath);
            if (backendCa.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                throw new InvalidOperationException("BackendCaCertificatePath must contain a CA certificate.");
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                    return false;
                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(backendCa);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(leaf);
            };
        }
        client = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public async Task ForwardAsync(HttpContext context, IHttpForwarder forwarder)
    {
        if (!context.Request.IsHttps) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var portal = context.Connection.LocalPort == options.PortalPort;
        if (context.Connection.LocalPort != options.Port && !portal) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var publicUri = portal ? new UriBuilder(options.PublicUrl) { Port = options.PortalPort }.Uri : new Uri(options.PublicUrl);
        if (portal && !ListenerPorts.PortalRoute(context.Request.Path, context.Request.Method))
        { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!string.Equals(context.Request.Host.Host, publicUri.Host, StringComparison.OrdinalIgnoreCase) ||
            (context.Request.Host.Port ?? ListenerPorts.DefaultManagementPort) != publicUri.Port)
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (!access.Allows(context.Request.Path, context.Connection.RemoteIpAddress))
        { ProxyRequestProtection.Reject(context, StatusCodes.Status404NotFound, "capability_or_network"); return; }
        if (!imagingEnabled && (context.Request.Path.StartsWithSegments("/api/imaging") || context.Request.Path.StartsWithSegments("/api/boot")))
        { ProxyRequestProtection.Reject(context, StatusCodes.Status404NotFound, "imaging_disabled"); return; }
        if (context.Request.Path == CloneDeploy_Agent.Contracts.SiteDirectDownload.Path && Downloads is not null)
        { await Downloads.ServeDirectAsync(context); return; }
        if (!portal && !SiteProxyProtocol.Allows(context.Request.Path)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!portal && !SiteProxyRoutes.Allows(context.Request.Path, context.Request.Method))
        { ProxyRequestProtection.Reject(context, StatusCodes.Status404NotFound, "route_or_method"); return; }
        // Do not allow accidentally chained proxies (or a backend DNS loop) to circulate requests.
        if (context.Request.Headers.ContainsKey(SiteProxyProtocol.VersionHeader)) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (AgentCertificateGate.RequiresCertificate(context.Request.Path, context.Request.Method) &&
            !agentCertificates.Validate(await context.Connection.GetClientCertificateAsync(context.RequestAborted), DateTimeOffset.UtcNow))
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (!await protection.AdmitAsync(context)) return;
        var transformer = new ProxyTransformer(access.StorageEnabled ? Downloads : null, false);
        await forwarder.SendAsync(context, options.BackendUrl, client, config, transformer);
        if (transformer.Retry && !context.Response.HasStarted)
        {
            context.Response.Clear();
            // A signed request is single-use at main. Only the agent can authorize a
            // fresh retry; never replay its proof to perform a storage fallback.
            if (context.Request.Headers.ContainsKey(CloneDeploy_Agent.Contracts.AgentSignatures.Header))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "1";
                return;
            }
            await forwarder.SendAsync(context, options.BackendUrl, client, config, new ProxyTransformer(access.StorageEnabled ? Downloads : null, true));
        }
    }

    public void Dispose() { client.Dispose(); identity.Dispose(); backendCa?.Dispose(); }

    private sealed class ProxyTransformer(SiteDownloadResponse? downloads, bool bypass) : HttpTransformer
    {
        public bool Retry { get; private set; }
        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken ct)
        {
            if (downloads is not null && !bypass && response?.StatusCode == HttpStatusCode.NoContent &&
                response.Headers.TryGetValues(SiteStorageProtocol.OfferHeader, out var values))
            {
                Retry = !await downloads.ServeAsync(context, values.Single());
                return false;
            }
            if (response is not null) response.Headers.Remove(SiteStorageProtocol.OfferHeader);
            return await base.TransformResponseAsync(context, response, ct);
        }

        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
            // Use the backend authority for Host/SNI. No untrusted forwarding headers cross this boundary.
            request.Headers.Host = null;
            foreach (var name in request.Headers.Select(x => x.Key).Where(SiteProxyProtocol.IsIdentityHeader).ToArray())
                request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(SiteProxyProtocol.VersionHeader, SiteProxyProtocol.Version);
            if (downloads is not null && HttpMethods.IsGet(context.Request.Method))
            {
                request.Headers.TryAddWithoutValidation(SiteStorageProtocol.CapabilityHeader, "1");
                if (bypass) request.Headers.TryAddWithoutValidation(SiteStorageProtocol.BypassHeader, "1");
            }
            request.Headers.TryAddWithoutValidation(SiteProxyProtocol.AddressHeader, context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0");
            var certificate = await context.Connection.GetClientCertificateAsync(cancellationToken);
            if (certificate is not null)
                request.Headers.TryAddWithoutValidation(SiteProxyProtocol.CertificateHeader, Convert.ToBase64String(certificate.RawData));
        }
    }
}
