namespace CloneDeploy_Agent.Contracts;

public sealed record AgentPortalSignInRequest(string LocalUser, string RedirectUri, string State, string CodeChallenge);
public sealed record AgentPortalSignInResponse(Guid RequestId, string SignInPath, DateTimeOffset ExpiresAtUtc) { public int BrowserPort { get; init; } }
public sealed record AgentPortalCodeRequest(Guid RequestId, string LocalUser, string Code, string CodeVerifier);
public sealed record AgentPortalSessionRequest(string LocalUser, string Token);
public sealed record AgentPortalUserSession(string Token, AgentDirectoryUser User, DateTimeOffset ExpiresAtUtc)
{
    public string? UserSid { get; init; }
}
public sealed record AgentPortalPolicyStart(AgentPortalSessionRequest Session, AgentPolicyStart Start);
public sealed record AgentPortalHelpRequest(string LocalUser, string Token, HelpRequest Request);
