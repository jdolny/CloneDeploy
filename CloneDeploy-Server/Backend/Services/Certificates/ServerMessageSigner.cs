using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ServerSigningCertificateDetails(
    string Subject, string Issuer, string Sha256Fingerprint, DateTime NotBeforeUtc, DateTime NotAfterUtc);

/// <summary>Main-server-only, automatically renewed signing identity. No signing key is sent to sites.</summary>
public sealed class ServerMessageSigner(IServiceScopeFactory scopes) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private X509Certificate2? certificate;

    /// <summary>Reads public metadata without creating or renewing the signing identity.</summary>
    public async Task<ServerSigningCertificateDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return certificate is null ? null : new(
                certificate.Subject, certificate.Issuer,
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
        }
        finally { gate.Release(); }
    }

    public async Task<string> SignAsync<T>(string context, T value, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (certificate is null || certificate.NotAfter.ToUniversalTime() < now.AddHours(1).UtcDateTime)
            {
                using var scope = scopes.CreateScope();
                await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
                var row = await db.AppCertificates.AsNoTracking().SingleAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked, ct);
                using var issuer = WebCertificateService.Load(row, scope.ServiceProvider.GetRequiredService<ISecretProtector>());
                var replacement = Issue(issuer, now);
                certificate?.Dispose();
                certificate = replacement;
            }
            return ServerSignatures.Sign(certificate, context, value, now);
        }
        finally { gate.Release(); }
    }

    public static X509Certificate2 Issue(X509Certificate2 issuer, DateTimeOffset now)
    {
        if (issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true ||
            issuer.NotBefore.ToUniversalTime() > now.UtcDateTime || issuer.NotAfter.ToUniversalTime() <= now.AddHours(1).UtcDateTime)
            throw new CryptographicException("A valid CloneDeploy CA is required for agent message signing.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(ServerSignatures.SigningSubject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerSignatures.SigningPurpose) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var rootKey = issuer.GetRSAPrivateKey() ?? throw new CryptographicException("Missing CA signing key.");
        var start = new DateTimeOffset(new[] { now.AddMinutes(-5).UtcDateTime, issuer.NotBefore.ToUniversalTime() }.Max());
        var end = new DateTimeOffset(new[] { now.AddDays(1).UtcDateTime, issuer.NotAfter.ToUniversalTime() }.Min());
        using var leaf = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1), start, end, RandomNumberGenerator.GetBytes(20));
        return leaf.CopyWithPrivateKey(key);
    }
    public void Dispose() { certificate?.Dispose(); gate.Dispose(); }
}
