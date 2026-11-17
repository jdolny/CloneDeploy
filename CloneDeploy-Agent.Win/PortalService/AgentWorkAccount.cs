using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

// Only the service sees remembered tokens. Callers are identified by the Windows pipe peer.
public sealed class AgentWorkAccount(AgentOptions options, AgentIdentity identity, AgentClient agent) : IDisposable
{
    private readonly AgentHttpClientPool pool = new(options);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Guid> pending = [];
    private HttpClient Client()
    {
        using var certificate = identity.GetClientCertificate() ?? throw new IOException("Agent is not enrolled.");
        var http = pool.Create(certificate); http.Timeout = TimeSpan.FromSeconds(10); return http;
    }
    private string PathFor(string sid) => Path.Combine(options.StateDirectory, "WorkAccounts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.GetServerUri().AbsoluteUri + "|" + identity.State.DeviceGuid + "|" + sid))) + ".protected");
    private string? Read(string sid)
    {
        var path = PathFor(sid);
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { File.Delete(path); return null; }
    }
    private void Save(string sid, string token)
    {
        var path = PathFor(sid); AgentStatePermissions.Protect(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<AgentPortalUserSession?> ValidateAsync(string sid, CancellationToken ct)
    {
        var token = Read(sid); if (token is null) return null;
        using var http = Client();
        using var response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/session", new AgentPortalSessionRequest("windows:" + sid, token), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { File.Delete(PathFor(sid)); return null; }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentPortalUserSession>(ct) ?? throw new IOException("Missing work session.");
    }
    public async Task<(string Sid, AgentPortalIdentity Identity)> ResolveAsync(string sid, CancellationToken ct)
    {
        var automatic = await agent.GetPortalIdentityAsync(sid, ct);
        if (automatic.Status != "NotFound") return (sid, automatic);
        await gate.WaitAsync(ct);
        try
        {
            var session = await ValidateAsync(sid, ct);
            return session?.UserSid is { Length: > 0 } workSid ? (workSid, new("Matched", session.User)) : (sid, automatic);
        }
        finally { gate.Release(); }
    }
    public async Task<AgentWorkAccountStatus> HandleAsync(string sid, AgentWorkAccountRequest input, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var local = "windows:" + sid;
            if (input.Operation == "sign-out")
            {
                pending.Remove(sid);
                var token = Read(sid);
                File.Delete(PathFor(sid));
                if (token is not null)
                {
                    using var http = Client();
                    using var response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/sign-out", new AgentPortalSessionRequest(local, token), ct);
                    response.EnsureSuccessStatusCode();
                }
                return new("SignedOut");
            }
            var automatic = await agent.GetPortalIdentityAsync(sid, ct);
            if (automatic.Status == "Matched") return new("Automatic", automatic.User);
            if (automatic.Status != "NotFound") return new("Unavailable");
            if (input.Operation == "status")
            {
                var session = await ValidateAsync(sid, ct);
                return session is null ? new("SignedOut") : new("SignedIn", session.User);
            }
            using var client = Client();
            if (input.Operation == "start" && input.Start is { } start)
            {
                using var response = await client.PostAsJsonAsync("api/agent/v1/portal-auth/start", start with { LocalUser = local }, ct);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<AgentPortalSignInResponse>(ct) ?? throw new IOException("Missing sign-in request.");
                if (result.BrowserPort is < 1 or > 65535 || result.SignInPath != $"/agent/portal-sign-in/{result.RequestId:D}") throw new IOException("Invalid sign-in address.");
                pending[sid] = result.RequestId;
                return new("Pending", Start: result, SignInUrl: new Uri(new UriBuilder(options.GetServerUri()) { Port = result.BrowserPort }.Uri, result.SignInPath.TrimStart('/')).AbsoluteUri);
            }
            if (input.Operation == "exchange" && input.Exchange is { } exchange && pending.GetValueOrDefault(sid) == exchange.RequestId && exchange.RequestId != Guid.Empty)
            {
                pending.Remove(sid);
                using var response = await client.PostAsJsonAsync("api/agent/v1/portal-auth/exchange", exchange with { LocalUser = local }, ct);
                response.EnsureSuccessStatusCode();
                var session = await response.Content.ReadFromJsonAsync<AgentPortalUserSession>(ct) ?? throw new IOException("Missing work session.");
                Save(sid, session.Token);
                return new("SignedIn", session.User);
            }
            throw new IOException("Invalid work-account request.");
        }
        finally { gate.Release(); }
    }
    public void Dispose() { pool.Dispose(); gate.Dispose(); }
}
