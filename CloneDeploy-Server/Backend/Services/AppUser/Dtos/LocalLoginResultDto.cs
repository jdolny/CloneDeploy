namespace CloneDeploy_Server.Backend.Services;

public sealed record LocalLoginResultDto(bool Succeeded = false, bool RequiresTwoFactor = false,
    bool IsLockedOut = false, bool RequiresEnrollment = false, bool Failed = false);
