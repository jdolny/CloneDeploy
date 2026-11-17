namespace CloneDeploy_Server.Backend.Services;

public sealed record CertificateAuthorityDetailsDto(string Subject, string Serial, string Sha256Fingerprint,
    DateTime NotBeforeUtc, DateTime NotAfterUtc, bool IsRevoked);
