namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentEnrollmentResponseDto(int SchemaVersion, Guid DeviceGuid, string CertificatePem, string CaCertificatePem, DateTime ExpiresAtUtc);
