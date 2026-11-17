using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy.SiteService;

public static class SiteListenerCertificates
{
    public static void ConfigurePortal(SiteServiceOptions options, IConfiguration? configuration)
    {
        if (configuration is null) return;
        if (new[] { "PortalCertificatePath", "PortalCertificatePassword", "PortalCertificateSubject", "PortalCertificateThumbprint" }
            .Any(key => !string.IsNullOrWhiteSpace(configuration[$"SiteService:{key}"])))
            throw new InvalidOperationException("Move SiteService portal certificate settings to Kestrel:Certificates:Default (Path/Password or Subject/Store/Location).");
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("Site Service listener addresses and ports are managed by main; do not configure Kestrel:Endpoints.");
        var certificate = configuration.GetSection("Kestrel:Certificates:Default");
        if (certificate.GetValue<bool>("AllowInvalid"))
            throw new InvalidOperationException("Site Service requires Kestrel:Certificates:Default:AllowInvalid to be false.");
        options.PortalCertificatePath = NullIfBlank(certificate["Path"]);
        options.PortalCertificatePassword = certificate["Password"];
        options.PortalCertificateSubject = NullIfBlank(certificate["Subject"]);
        options.PortalCertificateStore = NullIfBlank(certificate["Store"]) ?? "My";
        options.PortalCertificateStoreLocation = NullIfBlank(certificate["Location"]) ?? "LocalMachine";
        if (options.PortalCertificatePath is null && options.PortalCertificateSubject is null &&
            !string.IsNullOrEmpty(options.PortalCertificatePassword))
            throw new InvalidOperationException("Configure Kestrel:Certificates:Default:Path when supplying a certificate password.");
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static X509Certificate2 LoadService(SiteServiceOptions options)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.ServerCertificatePath, options.ServerCertificatePassword);
        try
        {
            using var ca = X509CertificateLoader.LoadCertificateFromFile(options.AgentCaCertificatePath);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!certificate.HasPrivateKey || !chain.Build(certificate) || chain.ChainElements.Count != 2 ||
                certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != false ||
                !certificate.MatchesHostname(new Uri(options.PublicUrl).Host))
                throw new InvalidOperationException("The service listener requires a current server certificate issued by the CloneDeploy CA for its public hostname.");
            return certificate;
        }
        catch { certificate.Dispose(); throw; }
    }

    public static X509Certificate2 LoadPortal(SiteServiceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PortalCertificateSubject))
            return X509CertificateLoader.LoadPkcs12FromFile(options.PortalCertificatePath ?? options.ServerCertificatePath,
                options.PortalCertificatePath is null ? options.ServerCertificatePassword : options.PortalCertificatePassword);

        if (options.PortalCertificatePath is not null || options.PortalCertificatePassword is not null)
            throw new InvalidOperationException("Configure either Subject or Path/Password under Kestrel:Certificates:Default, not both.");
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Portal certificate-store selection requires Windows. Use Kestrel:Certificates:Default:Path on other platforms.");
        if (!Enum.TryParse<StoreLocation>(options.PortalCertificateStoreLocation, true, out var location) ||
            !Enum.IsDefined(location))
            throw new InvalidOperationException("Kestrel:Certificates:Default:Location must be LocalMachine or CurrentUser.");
        var selected = Microsoft.AspNetCore.Server.Kestrel.Https.CertificateLoader.LoadFromStoreCert(
            options.PortalCertificateSubject.Trim(), options.PortalCertificateStore, location, allowInvalid: false);
        try
        {
            var now = DateTime.UtcNow;
            var usages = selected.Extensions.OfType<X509EnhancedKeyUsageExtension>();
            if (!selected.HasPrivateKey || now < selected.NotBefore.ToUniversalTime() || now > selected.NotAfter.ToUniversalTime() ||
                !selected.MatchesHostname(new Uri(options.PublicUrl).Host) ||
                usages.Any(e => !e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0")))
                throw new InvalidOperationException("The portal store certificate must have a private key, be current, match the public hostname, and support TLS server authentication.");
            try
            {
                using var rsa = selected.GetRSAPrivateKey();
                using var ecdsa = selected.GetECDsaPrivateKey();
                if (rsa is null && ecdsa is null)
                    throw new InvalidOperationException("The portal certificate requires an accessible RSA or ECDSA private key.");
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException("Cannot access the portal certificate private key. Grant the Site Service account read access using Manage Private Keys.", ex);
            }
            // Keep the store-backed key: never export or copy it into a PFX.
            return selected;
        }
        catch
        {
            selected.Dispose();
            throw;
        }
    }
}
