using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

public static class AgentCertificateLifecycle
{
    public static void ThrowIfExpired(string? certificatePem)
    {
        if (certificatePem is null) return;
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("The agent device certificate has expired. Automatic renewal requires a valid certificate. An administrator must remove the saved agent identity and reinstall/re-enroll the agent; ordinary repair preserves the expired identity.");
    }

    public static bool NeedsRenewal(string? pem)
    {
        if (pem is null) return false;
        ThrowIfExpired(pem);
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30);
    }

    public static void ValidateReplacement(string currentPem, string trustedCaPem, AgentCertificateRenewal renewal)
    {
        using var current = X509Certificate2.CreateFromPem(currentPem);
        using var replacement = X509Certificate2.CreateFromPem(renewal.CertificatePem);
        using var trusted = X509Certificate2.CreateFromPem(trustedCaPem);
        using var supplied = X509Certificate2.CreateFromPem(renewal.CaCertificatePem);
        if (!trusted.RawData.AsSpan().SequenceEqual(supplied.RawData) ||
            !current.SubjectName.RawData.AsSpan().SequenceEqual(replacement.SubjectName.RawData) ||
            current.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(replacement.PublicKey.ExportSubjectPublicKeyInfo()) ||
            replacement.NotAfter <= current.NotAfter)
            throw new InvalidDataException("Certificate renewal must retain the device identity and trusted CA, rotate the private key, and extend validity.");
        // Platform identity validation additionally verifies chain, usage, validity and local key.
    }
}
