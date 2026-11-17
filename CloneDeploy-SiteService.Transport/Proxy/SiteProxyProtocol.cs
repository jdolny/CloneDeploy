using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace CloneDeploy.SiteServices;

public static class SiteProxyProtocol
{
    public const int MaximumAuthenticationRequestsPerMinute = 1_000_000;
    public const int Sha256HexCharacters = 64;

    public const string VersionHeader = "X-CloneDeploy-Proxy";
    public const string CertificateHeader = "X-CloneDeploy-Client-Certificate";
    public const string AddressHeader = "X-CloneDeploy-Client-IP";
    public const string Version = "1";

    // Keep the browser/admin surface private. Boot HTTP, storage replicas and PXE are separate roles.
    public static bool Allows(PathString path) => path.StartsWithSegments("/api/agent") ||
        path.StartsWithSegments("/api/imaging") || path.StartsWithSegments("/api/boot");

    // These are application inputs, not assertions of proxy/device identity. Main validates
    // their tokens and binds them into its signed response. Preserve only this explicit list.
    private static readonly HashSet<string> AgentApplicationHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-CloneDeploy-Agent-Signature", "X-CloneDeploy-Signing-Nonce", "X-CloneDeploy-Admin-Token", "X-CloneDeploy-Windows-Sid",
        "X-CloneDeploy-Windows-Session", "X-CloneDeploy-ImagePrep-Uninstall", "X-CloneDeploy-Capture", "X-CloneDeploy-Imager-Signing"
    };

    public static bool IsIdentityHeader(string name) => (name.StartsWith("X-CloneDeploy-", StringComparison.OrdinalIgnoreCase) && !AgentApplicationHeaders.Contains(name)) ||
        name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-ARR-ClientCert", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Client-Cert", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase);

    public static bool IsFingerprint(string value) => value.Length == Sha256HexCharacters && value.All(Uri.IsHexDigit);

    public static bool IsCurrentClientCertificate(X509Certificate2 certificate, DateTimeOffset now) =>
        now.UtcDateTime >= certificate.NotBefore.ToUniversalTime() && now.UtcDateTime < certificate.NotAfter.ToUniversalTime() &&
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => !x.CertificateAuthority) &&
        certificate.Extensions.OfType<X509KeyUsageExtension>().Any(x => (x.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0) &&
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(x =>
            x.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));
}
