using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Holds the details and outcome of one policy run for recovery and reporting.
/// </summary>
public sealed record PolicyRunEntry(
    Guid RunId,
    Guid DeviceGuid,
    AgentPolicySnapshot Snapshot,
    string State,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    int? Result,
    string? Detail,
    bool Reported,
    string? UserName = null)
{
    public string? UserSid { get; init; }

    public bool SelfService { get; init; }
}