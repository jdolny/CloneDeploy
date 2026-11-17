using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Windows-authenticated login identity and the connection that runs actions as that user.</summary>
public sealed record LoginPolicyContext(
    string Sid,
    string UserName,
    string LogonKey,
    Func<AgentPolicyAction, string, CancellationToken, Task<PolicyProcessResult>> Execute)
{
    public int? RequestedPolicyId { get; init; }

    public Guid RequestId { get; init; }

    public int? Result { get; set; }
}