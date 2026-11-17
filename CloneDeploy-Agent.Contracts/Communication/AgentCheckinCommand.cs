namespace CloneDeploy_Agent.Contracts;

public sealed record AgentCheckinCommand(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc);
public sealed record AgentCheckinReceipt(Guid RequestId, bool Accepted);
