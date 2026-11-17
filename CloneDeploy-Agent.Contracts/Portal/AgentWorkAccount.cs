namespace CloneDeploy_Agent.Contracts;

public sealed record AgentWorkAccountRequest(string Operation, AgentPortalSignInRequest? Start = null, AgentPortalCodeRequest? Exchange = null);
public sealed record AgentWorkAccountStatus(string Status, AgentDirectoryUser? User = null,
    AgentPortalSignInResponse? Start = null, string? SignInUrl = null);
