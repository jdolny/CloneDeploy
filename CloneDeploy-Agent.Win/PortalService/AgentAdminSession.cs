using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Keeps browser proof keys and administrator tokens inside the SYSTEM service.</summary>
public sealed class AgentAdminSession(AgentOptions options, AgentIdentity identity) : IDisposable
{
    private const int MaximumCallbackHeaderBytes = 8192;
    private const int SecretBytes = 32;
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ToolRequestTimeout = TimeSpan.FromMinutes(1);

    private sealed class LocalSession
    {
        public readonly CancellationTokenSource Stop = new(SignInTimeout);
        public Task? Handoff;
        public AgentAdminSessionResponse? Session;
        public AgentAdminStatus Status = new("Pending", Message: "Finish signing in in your browser.");
    }

    private readonly object gate = new();
    private readonly AgentHttpClientPool http = new(options);
    private readonly Dictionary<(string Sid, int SessionId), LocalSession> local = [];

    private static string Secret() => Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private HttpClient Client()
    {
        using var certificate = identity.GetClientCertificate() ?? throw new IOException("The agent is not enrolled.");
        var client = http.Create(certificate);
        client.Timeout = RequestTimeout;

        return client;
    }

    public async Task<AgentAdminStatus> StartAsync(string sid, int sessionId, CancellationToken ct)
    {
        await SignOutAsync(sid, sessionId, ct);

        if (!AdminWindowsSession.IsUnlocked(sessionId))
            return new("SignedOut", Message: "Unlock your Windows session before signing in.");

        var state = Secret();
        var verifier = Secret();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var session = new LocalSession();

        try
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync(
                "api/agent/v1/admin/sign-in",
                new AgentAdminSignInRequest(
                    sid,
                    sessionId,
                    $"http://127.0.0.1:{port}/callback",
                    state,
                    Encode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))),
                ct);
            response.EnsureSuccessStatusCode();
            var request = await response.Content.ReadFromJsonAsync<AgentAdminSignInResponse>(ct)
                          ?? throw new IOException("The server returned no sign-in request.");

            if (request.BrowserPort is < 1 or > 65535 || request.BrowserPath != $"/agent/admin-sign-in/{request.RequestId:D}")
                throw new IOException("Invalid sign-in destination.");

            var browserUrl = new Uri(new UriBuilder(options.GetServerUri()) { Port = request.BrowserPort }.Uri, request.BrowserPath.TrimStart('/')).AbsoluteUri;
            session.Status = new(
                "Pending",
                ExpiresAt: request.ExpiresAt,
                SignInUrl: browserUrl,
                Message: "Finish signing in in your browser.");

            lock (gate)
            {
                if (local.Count >= 64)
                    throw new IOException("Too many local sign-in sessions.");

                local.Add((sid, sessionId), session);
            }

            session.Handoff = ReceiveAsync(listener, request.RequestId, state, verifier, sid, sessionId, session);

            return session.Status;
        }
        catch
        {
            listener.Stop();
            session.Stop.Dispose();
            throw;
        }
    }

    private async Task ReceiveAsync(
        TcpListener listener,
        Guid requestId,
        string state,
        string verifier,
        string sid,
        int sessionId,
        LocalSession localSession)
    {
        try
        {
            while (!localSession.Stop.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(localSession.Stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(localSession.Stop.Token);
                deadline.CancelAfter(CallbackTimeout);
                var stream = socket.GetStream();
                // A byte-bounded HTTP header prevents slow or unbounded loopback requests.
                var header = new List<byte>();
                var next = new byte[1];

                try
                {
                    while (header.Count < MaximumCallbackHeaderBytes)
                    {
                        if (await stream.ReadAsync(next, deadline.Token) == 0)
                            break;

                        header.Add(next[0]);

                        if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13
                            && header[^1] == 10)
                            break;
                    }
                }
                // A stalled or disconnected caller does not cancel the pending browser handoff.
                catch (OperationCanceledException) when (!localSession.Stop.IsCancellationRequested)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (header.Count < 4 || header[^4] != 13 || header[^3] != 10 || header[^2] != 13 || header[^1] != 10)
                    continue;

                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                if (header.Count >= MaximumCallbackHeaderBytes || first.Length != 3 || first[0] != "GET"
                    ||
                    !first[1].StartsWith("/callback?", StringComparison.Ordinal)
                    ||
                    !lines.Any(l => l.Equals($"Host: 127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var pairs = first[1][("/callback?".Length)..].Split('&').Select(p => p.Split('=', 2)).ToArray();

                if (pairs.Length != 2 || pairs.Any(p => p.Length != 2))
                    continue;

                var returnedState = pairs.FirstOrDefault(p => p[0] == "state")?[1];
                var code = pairs.FirstOrDefault(p => p[0] == "code")?[1];

                if (returnedState != state || code is null || code.Length != 43
                    || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    continue;

                if (!AdminWindowsSession.IsUnlocked(sessionId))
                    throw new OperationCanceledException();

                using var client = Client();
                using var response = await client.PostAsJsonAsync(
                    "api/agent/v1/admin/exchange",
                    new AgentAdminCodeRequest(requestId, code, verifier, sid, sessionId),
                    deadline.Token);
                response.EnsureSuccessStatusCode();
                var authenticated = await response.Content.ReadFromJsonAsync<AgentAdminSessionResponse>(deadline.Token)
                                    ?? throw new IOException("The server returned no administrator session.");

                lock (gate)
                {
                    localSession.Session = authenticated;

                    if (localSession.Stop.IsCancellationRequested)
                        throw new OperationCanceledException();

                    localSession.Stop.CancelAfter(SessionLifetime);
                    localSession.Status = new(
                        "SignedIn",
                        authenticated.UserName,
                        authenticated.ExpiresAt,
                        Message: "Administrator sign-in completed.")
                    {
                        Capabilities = authenticated.Capabilities
                    };
                }

                _ = MonitorAsync(sid, sessionId, localSession);
                const string body = "<!doctype html><html><head><meta charset='utf-8'><title>CloneDeploy</title></head><body><h1>Signed in to CloneDeploy</h1><p>You can close this tab and return to the agent UI.</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n");

                try
                {
                    await stream.WriteAsync(reply, deadline.Token);
                    await stream.WriteAsync(bytes, deadline.Token);
                }
                catch (IOException)
                {
                } // A browser closing its tab does not undo successful authentication.

                break;
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate)
                if (localSession.Session is null)
                    localSession.Status = new(
                        "SignedOut",
                        Message: "Sign-in expired or was cancelled. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or System.Text.Json.JsonException)
        {
            lock (gate)
                localSession.Status = new(
                    "SignedOut",
                    Message: "Administrator sign-in could not be completed. Check the server connection and try again.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<AgentAdminStatus> StatusAsync(string sid, int sessionId, CancellationToken ct)
    {
        LocalSession? found;

        lock (gate)
            local.TryGetValue((sid, sessionId), out found);

        if (found is null)
            return new("SignedOut");

        if (!AdminWindowsSession.IsUnlocked(sessionId))
        {
            await SignOutAsync(sid, sessionId, ct);

            return new(
                "SignedOut",
                Message: "Administrator access ended when the Windows session locked or disconnected.");
        }

        AgentAdminSessionResponse? session;

        lock (gate)
            session = found.Session;

        if (session is null)
        {
            lock (gate)
                return found.Status;
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await SignOutAsync(sid, sessionId, ct);

            return new("SignedOut", Message: "Administrator session expired.");
        }

        try
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync(
                "api/agent/v1/admin/session",
                new AgentAdminSessionRequest(session.Token, sid, sessionId),
                ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await SignOutAsync(sid, sessionId, ct);

                return new("SignedOut", Message: "Administrator access ended. Sign in again.");
            }

            response.EnsureSuccessStatusCode();
            var current = await response.Content.ReadFromJsonAsync<AgentAdminSessionResponse>(ct)
                          ?? throw new IOException("Invalid session response.");

            return new("SignedIn", current.UserName, current.ExpiresAt)
            {
                Capabilities = current.Capabilities
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new("Unavailable", Message: "Could not verify administrator access. Check the server connection.");
        }
    }

    private async Task MonitorAsync(string sid, int sessionId, LocalSession session)
    {
        try
        {
            while (!session.Stop.IsCancellationRequested)
            {
                await Task.Delay(SessionPollInterval, session.Stop.Token);

                if (AdminWindowsSession.IsUnlocked(sessionId) && session.Session?.ExpiresAt > DateTimeOffset.UtcNow)
                    continue;

                using var timeout = new CancellationTokenSource(RequestTimeout);
                await SignOutAsync(sid, sessionId, timeout.Token);

                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task<(HttpClient Client, CancellationToken Stop)> ToolClientAsync(
        string sid,
        int sessionId,
        string capability,
        CancellationToken ct)
    {
        var status = await StatusAsync(sid, sessionId, ct);

        if (status.Status != "SignedIn" || !status.Capabilities.Contains(capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Sign in as an administrator before using this tool.");

        lock (gate)
        {
            if (!local.TryGetValue( (sid, sessionId), out var found) || found.Session is null
                || found.Stop.IsCancellationRequested)
                throw new UnauthorizedAccessException("Administrator access ended. Sign in again.");

            var client = Client();
            client.Timeout = ToolRequestTimeout;
            client.DefaultRequestHeaders.Add("X-CloneDeploy-Admin-Token", found.Session.Token);
            client.DefaultRequestHeaders.Add("X-CloneDeploy-Windows-Sid", sid);
            client.DefaultRequestHeaders.Add(
                "X-CloneDeploy-Windows-Session",
                sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return (client, found.Stop.Token);
        }
    }

    internal async Task<HttpClient> DriverJobClientAsync(string sid, int sessionId, CancellationToken ct)
    {
        var lease = await ToolClientAsync(sid, sessionId, "CreateDriverPack", ct);
        var client = lease.Client;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Stop);
            var token = client.DefaultRequestHeaders.GetValues("X-CloneDeploy-Admin-Token").Single();
            using var response = await client.PostAsJsonAsync("api/agent/v1/admin/driver-job",
                new AgentAdminSessionRequest(token, sid, sessionId), linked.Token);
            response.EnsureSuccessStatusCode();
            var job = await response.Content.ReadFromJsonAsync<AgentAdminSessionResponse>(linked.Token)
                ?? throw new IOException("The server returned no job authorization.");
            client.DefaultRequestHeaders.Remove("X-CloneDeploy-Admin-Token");
            client.DefaultRequestHeaders.Add("X-CloneDeploy-Admin-Token", job.Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    internal static async Task EndDriverJobAsync(HttpClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var response = await client.PostAsJsonAsync("api/agent/v1/admin/sign-out",
                new AgentAdminSessionRequest(
                    client.DefaultRequestHeaders.GetValues("X-CloneDeploy-Admin-Token").Single(),
                    client.DefaultRequestHeaders.GetValues("X-CloneDeploy-Windows-Sid").Single(),
                    int.Parse(client.DefaultRequestHeaders.GetValues("X-CloneDeploy-Windows-Session").Single(),
                        System.Globalization.CultureInfo.InvariantCulture)), timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    }

    public async Task<AgentAdminStatus> SignOutAsync(string sid, int sessionId, CancellationToken ct)
    {
        LocalSession? found;

        lock (gate)
        {
            local.Remove((sid, sessionId), out found);
        }

        if (found is not null)
        {
            await found.Stop.CancelAsync();

            if (found.Handoff is not null)
                await found.Handoff;

            if (found.Session is { } session)
                try
                {
                    using var client = Client();
                    using var response = await client.PostAsJsonAsync(
                        "api/agent/v1/admin/sign-out",
                        new AgentAdminSessionRequest(session.Token, sid, sessionId),
                        ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                }

            found.Stop.Dispose();
        }

        return new("SignedOut");
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var session in local.Values)
                session.Stop.Cancel();

            local.Clear();
        }

        http.Dispose();
    }
}