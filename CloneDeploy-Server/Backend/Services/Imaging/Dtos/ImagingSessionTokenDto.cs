namespace CloneDeploy_Server.Backend.Services;

public sealed record ImagingSessionTokenDto(string TokenType, string AccessToken, int ExpiresIn);
