namespace CloneDeploy_Agent.Contracts;

public sealed record AgentAdminSignInRequest(string WindowsSid, int WindowsSessionId, string RedirectUri, string State, string CodeChallenge);
public sealed record AgentAdminSignInResponse(Guid RequestId, string BrowserPath, DateTimeOffset ExpiresAt) { public int BrowserPort { get; init; } }
public sealed record AgentAdminCodeRequest(Guid RequestId, string Code, string CodeVerifier, string WindowsSid, int WindowsSessionId);
public sealed record AgentAdminSessionRequest(string Token, string WindowsSid, int WindowsSessionId);
// Session tokens never leave the agent service through the local portal pipe.
public sealed record AgentAdminSessionResponse(string Token, string UserName, DateTimeOffset ExpiresAt, string[] Capabilities);
public sealed record AgentAdminStatus(string Status, string? UserName = null, DateTimeOffset? ExpiresAt = null,
    string? SignInUrl = null, string? Message = null)
{
    public string[] Capabilities { get; init; } = [];
}
public sealed record AgentDriverPackRequest(string Name);
public sealed record AgentDriverPackStatus(string Status, string Message, int Progress = 0, int? PackId = null, string? PackName = null);
public sealed record AgentDriverUploadRequest(string FileName, long Size);
public sealed record AgentDriverUploadSession(Guid Id, int ChunkSize);
public sealed record AgentDriverPackCreateRequest(Guid UploadId, string Name, string Manufacturer, string Model);
public sealed record AgentDriverPackCreated(int Id, string Name);
