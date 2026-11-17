namespace CloneDeploy_Server.Backend.Services;

public sealed record ImagingTokenPayloadDto(string UserId, string SecurityStamp, bool MfaVerified);
