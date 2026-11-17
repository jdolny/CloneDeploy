namespace CloneDeploy_Agent;

// One agent process owns the state directory. Serialize installers from policies and direct tasks.
/// <summary>
/// Keeps policies and direct deployments from running actions at the same time.
/// </summary>
internal static class AgentExecutionGate
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}