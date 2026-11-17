namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Indicates that an agent is waiting for enrollment approval.
/// </summary>
public sealed class AgentEnrollmentPendingException(AgentEnrollmentPendingDto pending) : Exception(pending.Status)
{
    public AgentEnrollmentPendingDto Pending { get; } = pending;
}
