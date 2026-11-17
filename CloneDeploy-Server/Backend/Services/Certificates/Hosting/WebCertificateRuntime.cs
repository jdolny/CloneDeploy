using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Loaded once after migrations, before Kestrel starts. Issuance/disable requires a restart.
public sealed class WebCertificateRuntime(IConfiguration? configuration = null, IWebHostEnvironment? environment = null) : IDisposable
{
    public X509Certificate2? Certificate { get; private set; }
    // The service listener always uses the CloneDeploy-issued certificate, even when
    // browser listeners use an externally configured HTTPS certificate.
    public void ConfigureService(Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions options)
    {
        options.OnAuthenticate = null;
        options.ServerCertificate = null;
        options.ServerCertificateSelector = (_, _) => Certificate;
    }
    public void Configure(Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions options)
    {
        if (configuration?.GetValue("Certificates:UseManagedHttps", true) == false) return;
        // Kestrel constructs explicit listeners before hosted startup loads the managed
        // certificate. Resolve it at handshake time rather than capturing a null value.
        if (environment?.IsDevelopment() == true)
        {
            // Keep Kestrel's development certificate fallback until a managed one exists.
            options.OnAuthenticate = (_, authentication) =>
            {
                if (Certificate is not { } certificate) return;
                authentication.ServerCertificateContext = null;
                authentication.ServerCertificateSelectionCallback = null;
                authentication.ServerCertificate = certificate;
            };
        }
        else
        {
            options.ServerCertificateSelector = (_, _) => Certificate ?? options.ServerCertificate;
        }
    }
    public async Task LoadAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var row = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Web && !x.IsRevoked);
        if (row is null) return;
        var root = await db.AppCertificates.AsNoTracking().SingleOrDefaultAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked)
            ?? throw new InvalidOperationException("The configured HTTPS certificate requires a valid CloneDeploy CA.");
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        using var issuer = WebCertificateService.Load(root, secrets);
        // Windows Schannel cannot use EphemeralKeySet. Without PersistKeySet the imported
        // platform key is cleaned up when this certificate is disposed at shutdown.
        var certificate = X509CertificateLoader.LoadPkcs12(row.PfxBlob, secrets.Unprotect(row.PasswordEncrypted), X509KeyStorageFlags.DefaultKeySet);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuer);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new(WebCertificateGenerator.ServerAuthenticationOid));
        var valid = chain.Build(certificate);
        var now = DateTime.UtcNow;
        // Expiration alone must not prevent administrators from reaching the renewal UI.
        // This is server startup validation only; clients still enforce certificate expiry.
        var expiredOnly = !valid && chain.ChainStatus.Length > 0 &&
            chain.ChainStatus.All(x => (x.Status & ~X509ChainStatusFlags.NotTimeValid) == 0) &&
            chain.ChainElements.Cast<X509ChainElement>().All(x => x.Certificate.NotBefore.ToUniversalTime() <= now) &&
            chain.ChainElements.Cast<X509ChainElement>().Any(x => x.Certificate.NotAfter.ToUniversalTime() <= now);
        if (!certificate.HasPrivateKey || (!valid && !expiredOnly))
        { certificate.Dispose(); throw new InvalidOperationException("The configured CloneDeploy service HTTPS certificate is invalid. Configure a valid CloneDeploy-issued certificate before restarting."); }
        if (expiredOnly)
            services.GetRequiredService<ILogger<WebCertificateRuntime>>().LogWarning("The managed HTTPS certificate or its CA has expired. Starting HTTPS for administrator recovery; clients will reject the expired chain. Renew the certificate in Settings > Certificate Authority.");
        Certificate = certificate;
    }
    public void Dispose() => Certificate?.Dispose();
}
