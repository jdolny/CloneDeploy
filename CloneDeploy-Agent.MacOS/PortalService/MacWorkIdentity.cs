using System.Net;
using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

// Only OS-authenticated portal peers can renew a local identity lease. Tokens remain in memory.
public sealed class MacWorkIdentity(MacConfiguration configuration, MacIdentity identity, string directory)
{
    private readonly object gate = new();
    private readonly Dictionary<uint, (string Token, DateTimeOffset Seen)> leases = [];
    private readonly Dictionary<uint, (string Token, AgentPortalUserSession Session, DateTimeOffset Checked)> cache = [];
    public Func<(uint Uid, string Name)?> ConsoleSession { get; init; } = MacConsoleSession.Current;
    public void Watch(uint uid, string token)
    {
        if (uid == 0 || token.Length != 43) return;
        lock (gate)
        {
            foreach (var key in leases.Where(x => DateTimeOffset.UtcNow - x.Value.Seen > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray())
            { leases.Remove(key); cache.Remove(key); }
            leases[uid] = (token, DateTimeOffset.UtcNow);
        }
    }
    public void Remove(uint uid) { lock (gate) { leases.Remove(uid); cache.Remove(uid); } }
    public async Task<AgentPortalUserSession?> ResolveAsync(uint uid, bool force, CancellationToken ct)
    {
        string token;
        lock (gate)
        {
            if (!leases.TryGetValue(uid, out var lease) || DateTimeOffset.UtcNow - lease.Seen >= TimeSpan.FromSeconds(60)) return null;
            token = lease.Token;
            if (!force && cache.TryGetValue(uid, out var saved) && saved.Token == token &&
                saved.Session.ExpiresAtUtc > DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - saved.Checked < TimeSpan.FromSeconds(30)) return saved.Session;
        }
        using var pool = new AgentHttpClientPool(configuration.Options(directory));
        using var cert = identity.GetClientCertificate(); using var http = pool.Create(cert);
        http.Timeout = TimeSpan.FromSeconds(10);
        using var response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/session", new AgentPortalSessionRequest($"macos:{uid}", token), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { Remove(uid); return null; }
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<AgentPortalUserSession>(ct);
        lock (gate)
        {
            if (!leases.TryGetValue(uid, out var lease) || lease.Token != token) return null;
            if (session?.UserSid is not { Length: > 0 } || session.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;
            cache[uid] = (token, session, DateTimeOffset.UtcNow);
        }
        return session;
    }
    public async Task RunPoliciesAsync(MacPolicyRunner policies, CancellationToken ct)
    {
        (uint, string)? last = null;
        try
        {
            while (true)
            {
                try
                {
                    var console = ConsoleSession();
                    var session = console is { } user ? await ResolveAsync(user.Uid, false, ct) : null;
                    if (session is null) last = null;
                    else if (last != (console!.Value.Uid, session.Token))
                    {
                        await policies.RunUserAsync(new($"macos:{console.Value.Uid}", session.Token), session, ct);
                        last = (console.Value.Uid, session.Token);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { Console.Error.WriteLine($"Work-account policy check will retry ({ex.GetType().Name})."); }
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
