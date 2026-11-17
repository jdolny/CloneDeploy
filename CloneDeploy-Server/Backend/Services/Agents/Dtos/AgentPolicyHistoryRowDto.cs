namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentPolicyHistoryRowDto(Guid Id, int PolicyId, string Name, string Hash,
    DateTime StartedAtUtc, DateTime? FinishedAtUtc, int? Result, string? Detail);
