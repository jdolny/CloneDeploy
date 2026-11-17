namespace CloneDeploy_Agent;

/// <summary>
/// Summarizes previous runs so the agent can decide whether a policy is due again.
/// </summary>
public sealed record PolicyHistoryState(
    DateTime? LastCompletedAtUtc,
    bool CompletedHash,
    bool InterruptedHash)
{
    public DateTime? LastStartedAtUtc { get; init; }
}
