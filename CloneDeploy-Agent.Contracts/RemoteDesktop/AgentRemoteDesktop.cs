namespace CloneDeploy_Agent.Contracts;

public enum RemoteAccessMode { RequireApproval, AllowUnattended, ApprovalWhenLoggedIn }
public enum RemoteConsentAction { Poll, Allow, Deny, Stop }
public sealed record RemoteConsentInput(Guid? SessionId = null, RemoteConsentAction Action = RemoteConsentAction.Poll);
public sealed record RemoteConsentStatus(Guid? SessionId, string Requester = "", bool Active = false, DateTimeOffset? ExpiresAt = null);

public sealed record AgentRemoteDesktopRequest(Guid SessionId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc)
{
    public int? WindowsSessionId { get; init; }
    public int ConsentProtocol { get; init; }
    public RemoteAccessMode AccessMode { get; init; }
    public string Requester { get; init; } = "";
}
public sealed record AgentRemoteDesktopReply(Guid SessionId, bool Accepted, string Detail);
public sealed record AgentDesktopSession(int Id, string UserName, string Connection, string State, bool Available)
{
    public int ConsentProtocol { get; init; }
}
public sealed record AgentDesktopSessionsRequest(Guid DeviceGuid, DateTimeOffset ExpiresAtUtc);
