using System.Net;
using CloneDeploy.SiteServices;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Yarp.ReverseProxy.Forwarder;

namespace CloneDeploy.SiteService;

public sealed class ManagedSiteService(SiteLocalStore store, ILogger<ManagedSiteService> logger, TimeProvider clock, SiteBootWorker boot) : BackgroundService
{
    private static readonly TimeSpan EnrollmentPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfigurationPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProxyShutdownTimeout = TimeSpan.FromSeconds(10);

    public SiteStorageWorker? Storage { get; init; }
    public SiteStorageCache? StorageCache { get; init; }
    public SiteMulticastWorker? Multicast { get; init; }
    public IConfiguration? BrowserConfiguration { get; init; }
    private WebApplication? listener;
    private X509Certificate2? serverCertificate;
    private X509Certificate2? portalCertificate;
    private int appliedRevision;
    private SiteConfiguration? proxyConfiguration;
    private string? lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var state = store.Load();
        if (state.Package.IsWindowsWorker) throw new InvalidDataException("A Windows Worker registration cannot run Site Service.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wasEnrolling = state.Configuration is null;
                try
                {
                    using var client = SiteEnrollmentClient.CreateClient(state, state.Configuration is not null);
                    using var response = state.Configuration is null
                        ? await client.PostAsJsonAsync("/api/site-services/enroll", SiteEnrollmentClient.Request(state), stoppingToken)
                        : await client.PostAsJsonAsync("/api/site-services/status", new SiteStatusRequest(appliedRevision,
                            listener is not null, lastError ?? Storage?.Error, SiteEnrollmentClient.Version) { Boot = boot.Status, HostOs = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other" }, stoppingToken);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        boot.Configure(null); Storage?.Configure(null); Multicast?.Configure(null);
                        await StopListener();
                        lastError = "Enrollment or service access was denied. Check approval, expiry and revocation on the main server.";
                        logger.LogWarning("{Message}", lastError);
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        var configuration = state.Configuration is null
                            ? (await response.Content.ReadFromJsonAsync<SiteEnrollmentReply>(stoppingToken))?.Configuration
                            : await response.Content.ReadFromJsonAsync<SiteConfiguration>(stoppingToken);
                        if (configuration is not null)
                        {
                            state = SiteEnrollmentClient.Accept(state, configuration, store);
                            listener?.Services.GetRequiredService<SiteProxy>().SetCapabilities(configuration.Access);
                            listener?.Services.GetRequiredService<SiteProxy>().SetProxyAccess(configuration.ImagingEnabled, configuration.AuthenticationRequestsPerMinute, configuration.ImagingLoginRequestsPerMinute ?? configuration.AuthenticationRequestsPerMinute);
                            listener?.Services.GetRequiredService<SiteProxy>().SetEnrollmentKeyHash(configuration.AgentProvisioningKeyHash);
                            boot.Configure(state); Storage?.Configure(configuration.Access.StorageEnabled ? state : null); Multicast?.Configure(configuration.Access.MulticastEnabled && configuration.Access.StorageEnabled ? state : null);
                            if (appliedRevision != configuration.Revision || proxyConfiguration?.PortalPort != configuration.PortalPort)
                            {
                                if (proxyConfiguration is null || proxyConfiguration.ProxyEnabled != configuration.ProxyEnabled ||
                                    proxyConfiguration.PortalPort != configuration.PortalPort || proxyConfiguration.ListenAddress != configuration.ListenAddress || proxyConfiguration.PublicUrl != configuration.PublicUrl ||
                                    proxyConfiguration.BackendUrl != configuration.BackendUrl || proxyConfiguration.IdentityCertificatePem != configuration.IdentityCertificatePem ||
                                    proxyConfiguration.HttpsCertificatePem != configuration.HttpsCertificatePem || listener is null)
                                {
                                    await StopListener();
                                    await StartListener(state, stoppingToken);
                                }
                                proxyConfiguration = configuration;
                                appliedRevision = configuration.Revision; lastError = null;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    lastError = SiteFailureDiagnostics.Describe(ex);
                    var endpoint = new Uri(state.Package.BackendUrl);
                    logger.LogWarning("SiteService {Operation} failed. Server={Host}:{Port}; connection IP={Address}. {Message}",
                        state.Configuration is null ? "enrollment" : "update", endpoint.Host, endpoint.Port, state.Package.BackendAddress, lastError);
                }
                // Report the newly applied configuration immediately after approval.
                if (wasEnrolling && state.Configuration is not null) continue;
                await Task.Delay(state.Configuration is null ? EnrollmentPollInterval : ConfigurationPollInterval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { boot.Configure(null); Storage?.Configure(null); Multicast?.Configure(null); await StopListener(); }
    }

    private async Task StartListener(SiteLocalState state, CancellationToken ct)
    {
        var configuration = state.Configuration!;
        var identityPath = Path.Combine(store.DirectoryPath, "identity.pfx");
        var httpsPath = Path.Combine(store.DirectoryPath, "https.pfx");
        var caPath = Path.Combine(store.DirectoryPath, "backend-ca.cer");
        // Export with the local key only here; private keys never travel in either direction.
        using (var identity = X509Certificate2.CreateFromPem(configuration.IdentityCertificatePem, state.IdentityKey))
            await File.WriteAllBytesAsync(identityPath, identity.Export(X509ContentType.Pfx, state.PfxPassword), ct);
        using (var https = X509Certificate2.CreateFromPem(configuration.HttpsCertificatePem, state.HttpsKey))
            await File.WriteAllBytesAsync(httpsPath, https.Export(X509ContentType.Pfx, state.PfxPassword), ct);
        using (var root = X509Certificate2.CreateFromPem(state.Package.CaCertificatePem))
            await File.WriteAllBytesAsync(caPath, root.RawData, ct);
        var options = new SiteServiceOptions { ListenAddress = configuration.ListenAddress, Port = new Uri(configuration.PublicUrl).Port,
            PortalPort = configuration.PortalPort, PublicUrl = configuration.PublicUrl, BackendUrl = configuration.BackendUrl, BackendAddress = state.Package.BackendAddress, IdentityCertificatePath = identityPath,
            IdentityCertificatePassword = state.PfxPassword, ServerCertificatePath = httpsPath, ServerCertificatePassword = state.PfxPassword,
            BackendCaCertificatePath = caPath, AgentCaCertificatePath = caPath,
            AuthenticationRequestsPerMinute = configuration.AuthenticationRequestsPerMinute,
            AgentProvisioningKeyHash = configuration.AgentProvisioningKeyHash,
            Access = configuration.Access, ImagingEnabled = configuration.ImagingEnabled, ImagingLoginRequestsPerMinute = configuration.ImagingLoginRequestsPerMinute };
        SiteListenerCertificates.ConfigurePortal(options, BrowserConfiguration);
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // The outer host owns the Windows service lifetime. This child owns only the current proxy listener.
        builder.Configuration.Sources.Clear();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning).AddFilter("Yarp.ReverseProxy", LogLevel.Warning);
        builder.Services.AddHttpForwarder(); builder.Services.AddSingleton(options); builder.Services.AddSingleton<SiteProxy>(_ => new(options) { Downloads = StorageCache is null ? null : new SiteDownloadResponse(StorageCache, state) });
        serverCertificate = SiteListenerCertificates.LoadService(options);
        try { portalCertificate = SiteListenerCertificates.LoadPortal(options); }
        catch { serverCertificate.Dispose(); serverCertificate = null; throw; }
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            k.Listen(IPAddress.Parse(configuration.ListenAddress), options.Port, endpoint => endpoint.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate; https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.AllowAnyClientCertificate();
            }));
            k.Listen(IPAddress.Parse(configuration.ListenAddress), options.PortalPort, endpoint => endpoint.UseHttps(https =>
            {
                https.ServerCertificate = portalCertificate; https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
            }));
        });
        var app = builder.Build();
        try
        {
            var proxy = app.Services.GetRequiredService<SiteProxy>();
            app.Map("/{**path}", (HttpContext context, IHttpForwarder forwarder) => proxy.ForwardAsync(context, forwarder));
            await app.StartAsync(ct); listener = app;
        }
        catch { await app.DisposeAsync(); serverCertificate.Dispose(); serverCertificate = null; portalCertificate?.Dispose(); portalCertificate = null; throw; }
    }

    private async Task StopListener()
    {
        if (listener is not null)
        {
            using var deadline = new CancellationTokenSource(ProxyShutdownTimeout);
            try { await listener.StopAsync(deadline.Token); } finally { await listener.DisposeAsync(); listener = null; }
        }
        serverCertificate?.Dispose(); serverCertificate = null; appliedRevision = 0;
        portalCertificate?.Dispose(); portalCertificate = null;
    }
}
