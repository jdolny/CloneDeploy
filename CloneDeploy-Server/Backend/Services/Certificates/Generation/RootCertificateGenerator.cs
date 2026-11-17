using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Creates the installation CA used to sign agent identities and HTTPS certificates.
/// </summary>
public static class RootCertificateGenerator
{
    public static ServiceResult<X509Certificate2> Generate(string organization, DateTimeOffset now)
    {
        organization = organization?.Trim() ?? string.Empty;
        if (organization.Length is < 1 or > 128 || organization.Any(char.IsControl))
            return ServiceResult<X509Certificate2>.Fail(ErrorKeys.CertificateAuthority.InvalidOrganization);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("CloneDeploy Root CA");
        subject.AddOrganizationName(organization);
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return ServiceResult<X509Certificate2>.Ok(request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(15)));
    }
}
