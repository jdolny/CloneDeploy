using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.Identity.Client;

namespace CloneDeploy_Server.Backend.Services;

public interface IEntraTokenProvider
{
    Task<string> GetGraphTokenAsync(EntraConnection settings, bool pending, CancellationToken ct);
}

public sealed class EntraCredentials(ISecretProtector secrets) : IEntraTokenProvider
{
    public X509Certificate2 Open(string encrypted) => X509CertificateLoader.LoadPkcs12(
        Convert.FromBase64String(secrets.Unprotect(encrypted)), null, X509KeyStorageFlags.EphemeralKeySet);

    public string Generate()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=CloneDeploy Entra application", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var bytes = certificate.Export(X509ContentType.Pfx);
        try { return secrets.Protect(Convert.ToBase64String(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<string> GetGraphTokenAsync(EntraConnection settings, bool pending, CancellationToken ct)
    {
        using var certificate = Open((pending ? settings.PendingCertificateEncrypted : settings.CertificateEncrypted)
            ?? throw new InvalidOperationException("Generate and activate an Entra certificate first."));
        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("The Entra application certificate has expired.");
        var client = ConfidentialClientApplicationBuilder.Create(settings.ClientId.ToString())
            .WithAuthority($"https://login.microsoftonline.com/{settings.TenantId:D}")
            .WithCertificate(certificate).Build();
        return (await client.AcquireTokenForClient(["https://graph.microsoft.com/.default"]).ExecuteAsync(ct)).AccessToken;
    }
}
