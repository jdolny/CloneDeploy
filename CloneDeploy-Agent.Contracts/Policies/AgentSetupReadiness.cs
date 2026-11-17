namespace CloneDeploy_Agent.Contracts;

public sealed record AgentSetupReadiness(bool Ready, string? Reason);
public sealed record AgentSetupReadinessReceipt(bool DeploymentPending);
