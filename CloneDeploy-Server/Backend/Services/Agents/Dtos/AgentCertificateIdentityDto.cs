namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentCertificateIdentityDto(Guid DeviceGuid, int DeviceId, Guid CertificateId, string DeviceName);
