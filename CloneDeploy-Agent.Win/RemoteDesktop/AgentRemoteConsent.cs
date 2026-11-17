using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Agent.RemoteDesktop;

namespace CloneDeploy_Agent;

/// <summary>Consent belongs to one Windows logon, never to a user-supplied session identifier.</summary>
public sealed class AgentRemoteConsent
{
    private readonly object gate = new();
    private Entry? current;
    private sealed class Entry(Guid id, int session, string key, string requester, DateTimeOffset expiry, Action stop)
    {
        public Guid Id = id;
        public int Session = session;
        public string Key = key, Requester = requester;
        public DateTimeOffset Expiry = expiry;
        public Action Stop = stop;
        public bool Active;
        public DateTimeOffset LastUi = DateTimeOffset.UtcNow;
        public TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public Task<bool> Begin(Guid id, int session, string key, string requester, DateTimeOffset expiry, Action stop)
    {
        lock (gate)
        {
            if (current is not null) throw new InvalidOperationException("Another remote request is active.");
            current = new(id, session, key, requester, expiry, stop);
            return current.Decision.Task;
        }
    }
    public void Activate(Guid id) { lock (gate) { if (current?.Id == id) current.Active = true; } }
    public bool UiAvailable(Guid id) { lock (gate) return current?.Id == id && (!current.Active || DateTimeOffset.UtcNow - current.LastUi < TimeSpan.FromSeconds(30)); }
    public void End(Guid id) { lock (gate) { if (current?.Id == id) { current.Decision.TrySetResult(false); current = null; } } }
    public RemoteConsentStatus Handle(int session, string key, RemoteConsentInput input)
    {
        Action? stop = null;
        RemoteConsentStatus result;
        lock (gate)
        {
            var entry = current;
            if (entry is null || entry.Session != session || entry.Key != key || string.IsNullOrEmpty(key)) return new(null);
            entry.LastUi = DateTimeOffset.UtcNow;
            if (input.SessionId == entry.Id)
            {
                if (!entry.Active && entry.Expiry > DateTimeOffset.UtcNow && input.Action is RemoteConsentAction.Allow or RemoteConsentAction.Deny)
                    entry.Decision.TrySetResult(input.Action == RemoteConsentAction.Allow);
                if (input.Action == RemoteConsentAction.Stop || input.Action == RemoteConsentAction.Deny)
                { entry.Decision.TrySetResult(false); stop = entry.Stop; }
            }
            result = !entry.Active && entry.Expiry <= DateTimeOffset.UtcNow ? new(null)
                : new(entry.Id, entry.Requester, entry.Active, entry.Active ? null : entry.Expiry);
        }
        stop?.Invoke();
        return result;
    }
    public static string IdentityKey(WindowsIdentity identity)
    {
        if (identity.User is null) return "";
        // WindowsIdentity.Groups omits logon SID entries. AuthenticationId is shared
        // by the primary and impersonation tokens for this logon, and changes at logon.
        if (!GetTokenInformation(identity.AccessToken, 10, out var statistics, Marshal.SizeOf<TokenStatistics>(), out _))
            throw new System.ComponentModel.Win32Exception();
        return $"{identity.User.Value}:{statistics.AuthenticationId.High:x8}{statistics.AuthenticationId.Low:x8}";
    }
    public static string SessionKey(DesktopSessions.Session selected)
    {
        if (string.IsNullOrEmpty(selected.UserName)) return "";
        if (!WTSQueryUserToken((uint)selected.Id, out var token)) throw new System.ComponentModel.Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var key = IdentityKey(identity);
            return key.Length > 0 ? key : throw new UnauthorizedAccessException("Windows logon identity is unavailable.");
        }
    }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId;
        public long Expiration;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public Luid ModifiedId;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out TokenStatistics information, int length, out int returned);
}
