using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy_Server.Backend.Services;

public static class WebCertificateGenerator
{
    public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    public static string[] Hosts(string addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses) || addresses.Length > 8192) throw new InvalidDataException("Enter at least one HTTPS URL, DNS name, or IP address.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in addresses.Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string host;
            if (IPAddress.TryParse(value, out var direct)) host = direct.ToString();
            else
            {
                if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new InvalidDataException("Use HTTPS URLs without credentials, queries or fragments, or plain DNS/IP names.");
                host = uri.IdnHost.Trim('[', ']');
            }
            if (host.Contains('%') || host.Contains('*') || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new InvalidDataException("Enter exact DNS names or IP addresses; wildcards and scoped addresses are unsupported.");
            result.Add(host);
            if (result.Count > 32) throw new InvalidDataException("Enter no more than 32 distinct host names or addresses.");
        }
        if (result.Count == 0) throw new InvalidDataException("Enter at least one server address.");
        return result.ToArray();
    }

    public static X509Certificate2 Generate(X509Certificate2 issuer, string addresses, DateTimeOffset now)
    {
        var hosts = Hosts(addresses);
        if (!issuer.HasPrivateKey || issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true ||
            now.UtcDateTime < issuer.NotBefore.ToUniversalTime() || now.AddDays(1).UtcDateTime >= issuer.NotAfter.ToUniversalTime())
            throw new InvalidDataException("The CA must be valid and have a signing key with at least one day remaining.");
        using var key = RSA.Create(3072);
        var subject = new X500DistinguishedNameBuilder(); subject.AddCommonName("CloneDeploy HTTPS");
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthenticationOid) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts) { if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(host); }
        request.CertificateExtensions.Add(san.Build());
        var start = now.AddMinutes(-5).UtcDateTime < issuer.NotBefore.ToUniversalTime() ? new DateTimeOffset(issuer.NotBefore.ToUniversalTime()) : now.AddMinutes(-5);
        var end = now.AddDays(365).UtcDateTime > issuer.NotAfter.ToUniversalTime() ? new DateTimeOffset(issuer.NotAfter.ToUniversalTime()) : now.AddDays(365);
        using var certificate = request.Create(issuer, start, end, RandomNumberGenerator.GetBytes(20));
        return certificate.CopyWithPrivateKey(key);
    }
}
