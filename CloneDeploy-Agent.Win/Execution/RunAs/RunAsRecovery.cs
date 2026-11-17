namespace CloneDeploy_Agent;

/// <summary>
/// Identifies a temporary run and the account changes that may still need cleanup.
/// </summary>
public sealed record RunAsRecovery(Guid Id, string AccountSid, string MembershipState);