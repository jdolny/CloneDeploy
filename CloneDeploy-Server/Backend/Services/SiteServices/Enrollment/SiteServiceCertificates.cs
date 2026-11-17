using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public static class SiteServiceCertificates
{
    public static void Issue(SiteServiceRegistration service, X509Certificate2 issuer, DateTimeOffset now)
    {
        var end = now.AddDays(90) < issuer.NotAfter.ToUniversalTime() ? now.AddDays(90) : new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        if (end < now.AddDays(2) || issuer.NotBefore.ToUniversalTime() > now.UtcDateTime)
            throw new InvalidDataException("The CA must be valid with at least two days remaining.");
        var start = now.AddMinutes(-5) > issuer.NotBefore.ToUniversalTime() ? now.AddMinutes(-5) : new DateTimeOffset(issuer.NotBefore.ToUniversalTime());
        X509Certificate2 Sign(string csr, bool server)
        {
            // Ignore all requester-supplied extensions, subjects and SANs.
            var parsed = SiteEnrollmentCrypto.ReadCsr(csr);
            var request = new CertificateRequest(new X500DistinguishedName($"CN=CloneDeploy SiteService {service.Id:D}"),
                parsed.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            if (server)
            {
                var san = new SubjectAlternativeNameBuilder();
                var host = new Uri(service.PublicUrl).Host.Trim('[', ']');
                if (System.Net.IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(host);
                request.CertificateExtensions.Add(san.Build());
            }
            return request.Create(issuer, start, end, RandomNumberGenerator.GetBytes(20));
        }
        using var identity = Sign(service.IdentityCsr!, false);
        using var https = Sign(service.HttpsCsr!, true);
        service.PreviousCertificateSha256 = service.CertificateSha256;
        service.PreviousCertificateUntilUtc = service.CertificateSha256 is null ? null : now.AddHours(24).UtcDateTime;
        service.IdentityCertificate = identity.RawData;
        service.HttpsCertificate = https.RawData;
        service.CertificateSha256 = identity.GetCertHashString(HashAlgorithmName.SHA256);
        service.CertificateExpiresAtUtc = identity.NotAfter.ToUniversalTime();
    }
}
