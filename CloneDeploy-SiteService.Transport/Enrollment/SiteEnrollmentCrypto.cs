using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy.SiteServices;

public static class SiteEnrollmentCrypto
{
    private const int MinimumCsrCharacters = 100;
    private const int MaximumCsrCharacters = 8192;
    private const int MinimumRsaKeyBits = 2048;
    private const int MaximumRsaKeyBits = 4096;
    private const int ProofClockSkewMinutes = 5;
    private const int MaximumSignatureCharacters = 1024;

    public static CertificateRequest ReadCsr(string pem)
    {
        if (pem is null || pem.Length is < MinimumCsrCharacters or > MaximumCsrCharacters) throw new InvalidDataException("Invalid certificate request.");
        var csr = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var key = csr.PublicKey.GetRSAPublicKey();
        if (key is null || key.KeySize is < MinimumRsaKeyBits or > MaximumRsaKeyBits) throw new InvalidDataException("Use an RSA key between 2048 and 4096 bits.");
        return csr;
    }

    public static string KeyFingerprint(CertificateRequest csr) => Convert.ToHexString(SHA256.HashData(csr.PublicKey.ExportSubjectPublicKeyInfo()));

    public static void Verify(SiteEnrollmentRequest request, DateTimeOffset now)
    {
        if (request.Timestamp < now.AddMinutes(-ProofClockSkewMinutes).ToUnixTimeSeconds() || request.Timestamp > now.AddMinutes(ProofClockSkewMinutes).ToUnixTimeSeconds())
            throw new InvalidDataException("Enrollment proof expired. Check the service clock.");
        using var key = ReadCsr(request.IdentityCsr).PublicKey.GetRSAPublicKey()!;
        if (request.Signature is null || request.Signature.Length > MaximumSignatureCharacters ||
            !key.VerifyData(request.SigningBytes(), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Invalid enrollment proof.");
        if (KeyFingerprint(ReadCsr(request.IdentityCsr)) == KeyFingerprint(ReadCsr(request.HttpsCsr)))
            throw new InvalidDataException("Use separate identity and HTTPS keys.");
    }
}
