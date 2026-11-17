namespace CloneDeploy_Agent.Contracts;

public enum AgentActivityKind { Idle, CheckingIn, EvaluatingPolicies, RunningTask, RunningPolicy, CollectingInventory }

// A complete snapshot, ordered within a live connection. Names are resolved by the server.
public sealed record AgentActivity(long Sequence, AgentActivityKind Kind, Guid? RunId = null);
