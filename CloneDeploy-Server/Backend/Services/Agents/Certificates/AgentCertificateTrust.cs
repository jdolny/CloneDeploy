using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Only public root bytes are cached. Device/issuer revocation is checked in the database on every API request.
/// <summary>
/// Checks whether an agent's identity certificate comes from a trusted source.
/// </summary>
public sealed class AgentCertificateTrust
{
    public const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private byte[]? _root;
    public void SetRoot(byte[]? certificateDer) => Volatile.Write(ref _root, certificateDer?.ToArray());

    public bool Validate(X509Certificate2 certificate, DateTimeOffset now)
    {
        var bytes = Volatile.Read(ref _root);
        if (bytes is null) return false;
        try
        {
            if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != false ||
                certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault()?.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
                certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault()?.EnhancedKeyUsages
                    .Cast<Oid>().Any(x => x.Value == ClientAuthenticationOid) != true) return false;
            using var root = X509CertificateLoader.LoadCertificate(bytes);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
            chain.ChainPolicy.DisableCertificateDownloads = true;
            // This private CA has no CRL/OCSP endpoint. Registered certificate and issuer status are checked separately.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            return chain.Build(certificate) && chain.ChainElements.Count == 2;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException) { return false; }
    }
}
