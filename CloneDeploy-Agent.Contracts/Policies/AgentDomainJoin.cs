namespace CloneDeploy_Agent.Contracts;

public enum DomainJoinOuMode { DomainDefault, SpecificOu, NamingRules }
public sealed record AgentDomainJoinRequest(string ComputerName);
public sealed record AgentDomainJoinPlan(bool Success, string? OrganizationalUnit, string Message, AgentPolicyCredential? Credential);
