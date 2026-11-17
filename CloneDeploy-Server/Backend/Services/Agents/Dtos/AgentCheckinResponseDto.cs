namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentCheckinResponseDto(int SchemaVersion, Guid DeviceGuid, DateTime ServerTimeUtc,
    int CheckinIntervalSeconds, bool UserLoginsSubmitted = false,
    CloneDeploy_Agent.Contracts.AgentUpdateOffer? Update = null, string? BrandingVersion = null, CloneDeploy_Agent.Contracts.AgentFeatures? Features = null, string Platform = CloneDeploy_Agent.Contracts.AgentPlatform.Windows, CloneDeploy_Agent.Contracts.AgentUpdateOffer? MacUpdate = null,
    CloneDeploy_Agent.Contracts.AgentCertificateRenewal? CertificateRenewal = null);
