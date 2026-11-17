using System.ComponentModel.DataAnnotations;

namespace CloneDeploy_Agent.Contracts;

public sealed record AgentTaskClaim(bool RegularCheckin);
public sealed record AgentTaskOffer(Guid TaskId, Guid LeaseToken, DateTime LeaseExpiresAtUtc, string ActionJson, string ContentHash);
public sealed record AgentTaskLease(Guid LeaseToken);
public sealed record AgentTaskResult(Guid LeaseToken, bool Success, [Required, StringLength(4000)] string Detail, bool Interrupted = false);
