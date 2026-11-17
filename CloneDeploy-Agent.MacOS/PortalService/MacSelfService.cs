using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

public sealed class MacSelfService(MacConfiguration configuration, MacIdentity identity, string directory, MacPolicyRunner policies, CancellationToken stopping)
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumIconBytes = 65536;
    private const int MaximumRememberedRequests = 100;

    private readonly object gate = new();
    private Task worker = Task.CompletedTask;
    private readonly Dictionary<Guid, (uint Uid, string Sid, MacSelfServiceReply Reply)> requests = [];
    public Func<(uint Uid, string Name)?> ConsoleSession { get; init; } = MacConsoleSession.Current;
    public Task Completion { get { lock (gate) return worker; } }
    public async Task<MacSelfServiceReply> HandleAsync(uint uid, MacPortalAuthRequest request, CancellationToken ct)
    {
        if (ConsoleSession()?.Uid != uid || uid == 0 || request.Token is not { Length: 43 }) return new("SignedOut", "Sign in with your work account to use self service.");
        using var pool = new AgentHttpClientPool(configuration.Options(directory));
        using var certificate = identity.GetClientCertificate(); using var http = pool.Create(certificate);
        http.Timeout = TimeSpan.FromSeconds(10); http.MaxResponseContentBufferSize = MaximumResponseBytes;
        var sessionRequest = new AgentPortalSessionRequest($"macos:{uid}", request.Token);
        using var response = await http.PostAsJsonAsync("api/agent/v1/portal-auth/session", sessionRequest, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return new("SignedOut", "Your session ended. Sign in again.");
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<AgentPortalUserSession>(ct);
        if (session?.UserSid is not { Length: > 0 } sid) return new("SignedOut");
        if (request.Operation == "self-service-status")
        {
            lock (gate)
            {
                if (requests.TryGetValue(request.RequestId, out var saved))
                {
                    if (saved.Uid != uid || saved.Sid != sid) return new("Unavailable");
                    var active = policies.RequestedRun(request.RequestId, sid);
                    return saved.Reply.Status == "Queued" && active is not null ? Result(active, request.RequestId) : saved.Reply;
                }
            }
            var run = policies.RequestedRun(request.RequestId, sid);
            return run is null ? new("Unavailable", "No result is available. Review policy history before running again.", RequestId: request.RequestId)
                : Result(run, request.RequestId);
        }
        var catalog = await http.GetFromJsonAsync<AgentPortalPolicy[]>("api/agent/v1/portal/available?sid=" + Uri.EscapeDataString(sid), ct) ?? [];
        if (request.Operation == "self-service") return new("Catalog", Policies: catalog);
        if (request.Operation == "self-service-icon")
        {
            var hash = catalog.FirstOrDefault(x => x.Id == request.PolicyId)?.IconHash;
            if (hash is null) return new("Unavailable");
            var bytes = await http.GetByteArrayAsync($"api/agent/v1/portal/icons/{request.PolicyId}?sid={Uri.EscapeDataString(sid)}", ct);
            if (bytes.Length > MaximumIconBytes || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return new("Unavailable");
            return new("Icon", Icon: bytes);
        }
        if (request.Operation != "self-service-run" || request.RequestId == Guid.Empty || request.PolicyId <= 0) return new("InvalidRequest");
        if (!catalog.Any(x => x.Id == request.PolicyId)) return new("Unavailable", "This policy is no longer available.");
        lock (gate)
        {
            if (requests.TryGetValue(request.RequestId, out var prior)) return prior.Uid == uid && prior.Sid == sid ? prior.Reply : new("Unavailable");
            var run = policies.RequestedRun(request.RequestId, sid);
            if (run is not null) return Result(run, request.RequestId);
            if (!worker.IsCompleted) return new("Busy", "Another self-service request is already queued or running.");
            if (stopping.IsCancellationRequested) return new("Unavailable", "The device service is restarting.");
            if (requests.Count >= MaximumRememberedRequests) requests.Remove(requests.Keys.First());
            var queued = new MacSelfServiceReply("Queued", "Waiting for the current device activity to finish…", RequestId: request.RequestId);
            requests.Add(request.RequestId, (uid, sid, queued));
            worker = Task.Run(async () =>
            {
                MacSelfServiceReply result;
                try
                {
                    await policies.RunRequestedAsync(sessionRequest, session, request.PolicyId, request.RequestId, stopping);
                    var completed = policies.RequestedRun(request.RequestId, sid);
                    result = completed is null ? new("Failed", "The request could not be started.", RequestId: request.RequestId) : Result(completed, request.RequestId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Self-service request {request.RequestId} failed ({ex.GetType().Name}).");
                    result = new("Failed", "The request could not complete. Review policy history before running again.", RequestId: request.RequestId);
                }
                lock (gate) requests[request.RequestId] = (uid, sid, result);
            });
            return queued;
        }
    }
    private static MacSelfServiceReply Result(PolicyRunEntry run, Guid id) => new(
        run.FinishedAtUtc is null ? "Running" : "Completed",
        run.FinishedAtUtc is null ? "Running…" : run.Result == (int)PolicyRunResult.Success ? "Completed successfully." : $"Finished: {(PolicyRunResult?)run.Result}.",
        RequestId: id, Result: run.Result);
}
