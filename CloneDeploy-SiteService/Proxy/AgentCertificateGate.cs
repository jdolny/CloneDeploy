using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

// Edge admission only. The main server still checks registration, revocation and authorization.
public sealed class AgentCertificateGate
{
    private readonly byte[] rootBytes;

    public AgentCertificateGate(string certificatePath)
    {
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException("AgentCaCertificatePath is required for proxy agent authentication.");
        using var root = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        if (root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true ||
            root.HasPrivateKey)
            throw new InvalidOperationException("AgentCaCertificatePath must contain a public CA certificate.");
        rootBytes = root.RawData;
    }

    public static bool RequiresCertificate(PathString path, string method) =>
        path.StartsWithSegments("/api/agent") &&
        !(HttpMethods.IsPost(method) && string.Equals(path.Value?.TrimEnd('/'), "/api/agent/v1/enroll", StringComparison.OrdinalIgnoreCase));

    public bool Validate(X509Certificate2? certificate, DateTimeOffset now)
    {
        if (certificate is null) return false;
        try
        {
            if (!SiteProxyProtocol.IsCurrentClientCertificate(certificate, now)) return false;
            using var root = X509CertificateLoader.LoadCertificate(rootBytes);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            chain.ChainPolicy.DisableCertificateDownloads = true;
            // The private CA has no CRL/OCSP endpoint; the main server checks revocation.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            return chain.Build(certificate) && chain.ChainElements.Count == 2;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        { return false; }
    }
}
