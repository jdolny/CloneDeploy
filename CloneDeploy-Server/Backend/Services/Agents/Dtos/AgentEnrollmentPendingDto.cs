namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentEnrollmentPendingDto(int SchemaVersion, Guid RequestId, string Status, int RetryAfterSeconds);
