using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Short-lived, device-bound administrator sessions. A server restart invalidates them.</summary>
public sealed class AgentAdminSessions(TimeProvider clock)
{
    private sealed record Pending(Guid DeviceGuid, AgentAdminSignInRequest Request, DateTimeOffset ExpiresAt)
    {
        public string? CodeHash { get; set; }
        public int? UserId { get; set; }
        public string? SecurityStamp { get; set; }
        public bool EntraAuthenticated { get; set; }
    }
    private sealed record Session(Guid DeviceGuid, string WindowsSid, int WindowsSessionId, int UserId,
        string? SecurityStamp, DateTimeOffset ExpiresAt, bool EntraAuthenticated, string? JobCapability = null);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Pending> pending = [];
    private readonly Dictionary<string, Session> sessions = [];
    private static async Task<string[]> CapabilitiesAsync(CloneDeployDbContext db, int userId, CancellationToken ct)
    {
        if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.AgentAdminTools, ct)) return [];
        if (await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.AdminOnly, ct)) return ["CreateDriverPack", "RunImagePrep", "CreateBootableUsb", "CreateCaptureImage"];
        var result = new List<string> { "RunImagePrep", "CreateBootableUsb" };
        if (await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageCapture, ct))
        {
            if (await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageCreate, ct)) result.Add("CreateCaptureImage");
        }
        if (await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.DriverPackEdit, ct)) result.Add("CreateDriverPack");
        return result.ToArray();
    }
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Encode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Secret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    public AgentAdminSignInResponse Start(Guid deviceGuid, AgentAdminSignInRequest request)
    {
        if (request.WindowsSid is null || request.WindowsSid.Length > 184 || !request.WindowsSid.StartsWith("S-1-", StringComparison.Ordinal) ||
            request.WindowsSessionId < 0 || !Secret(request.State) || !Secret(request.CodeChallenge) ||
            !Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" ||
            redirect.Host != "127.0.0.1" || redirect.Port < 1024 || redirect.AbsolutePath != "/callback" ||
            redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0)
            throw new ArgumentException("Invalid administrator sign-in request.");
        lock (gate)
        {
            Cleanup();
            foreach (var old in pending.Where(p => p.Value.DeviceGuid == deviceGuid && p.Value.Request.WindowsSid == request.WindowsSid &&
                p.Value.Request.WindowsSessionId == request.WindowsSessionId).Select(p => p.Key).ToArray()) pending.Remove(old);
            if (pending.Count >= 1024) throw new InvalidOperationException("Too many sign-in requests. Try again shortly.");
            var id = Guid.NewGuid();
            var expires = clock.GetUtcNow().AddMinutes(5);
            pending.Add(id, new(deviceGuid, request, expires));
            return new(id, $"/agent/admin-sign-in/{id:D}", expires);
        }
    }

    public Guid? PendingDevice(Guid id)
    {
        lock (gate) { Cleanup(); return pending.TryGetValue(id, out var request) && request.CodeHash is null ? request.DeviceGuid : null; }
    }
    public Task<string?> CompleteAsync(Guid id, int userId, ICloneDeployDbFactory factory, CancellationToken ct) =>
        CompleteAsync(id, userId, factory, ct, false);

    public async Task<string?> CompleteAsync(Guid id, int userId, ICloneDeployDbFactory factory, CancellationToken ct, bool entraAuthenticated)
    {
        await using var db = await factory.CreateContextAsync();
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || (await CapabilitiesAsync(db, user.Id, ct)).Length == 0 ||
            (user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()) || (!entraAuthenticated && await MfaPolicy.RequiresWebAsync(db, user) && !user.TwoFactorEnabled)) return null;
        lock (gate)
        {
            Cleanup();
            if (!pending.TryGetValue(id, out var request) || request.CodeHash is not null) return null;
            var code = Encode(RandomNumberGenerator.GetBytes(32));
            request.CodeHash = Hash(code); request.UserId = user.Id; request.SecurityStamp = user.SecurityStamp;
            request.EntraAuthenticated = entraAuthenticated;
            return request.Request.RedirectUri + "?code=" + code + "&state=" + request.Request.State;
        }
    }

    public async Task<AgentAdminSessionResponse?> ExchangeAsync(Guid deviceGuid, AgentAdminCodeRequest input,
        ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Secret(input.Code) || !Secret(input.CodeVerifier)) return null;
        Pending request;
        lock (gate)
        {
            Cleanup();
            if (!pending.TryGetValue(input.RequestId, out request!) || request.DeviceGuid != deviceGuid ||
                request.Request.WindowsSid != input.WindowsSid || request.Request.WindowsSessionId != input.WindowsSessionId ||
                request.CodeHash is null || !Equal(request.CodeHash, Hash(input.Code)) || !Equal(request.Request.CodeChallenge, Hash(input.CodeVerifier))) return null;
            pending.Remove(input.RequestId); // One-time code, including concurrent redemption attempts.
        }
        await using var db = await factory.CreateContextAsync();
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null || (await CapabilitiesAsync(db, user.Id, ct)).Length == 0 || user.SecurityStamp != request.SecurityStamp ||
            (user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()) || (!request.EntraAuthenticated && await MfaPolicy.RequiresWebAsync(db, user) && !user.TwoFactorEnabled)) return null;
        var token = Encode(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddMinutes(15);
        lock (gate)
        {
            Cleanup();
            if (sessions.Count >= 4096) return null;
            sessions.Add(Hash(token), new(deviceGuid, input.WindowsSid, input.WindowsSessionId, user.Id, user.SecurityStamp, expires, request.EntraAuthenticated));
        }
        return new(token, user.UserName ?? "Administrator", expires, await CapabilitiesAsync(db, user.Id, ct));
    }

    public Task<AgentAdminSessionResponse?> ValidateAsync(Guid deviceGuid, AgentAdminSessionRequest input,
        ICloneDeployDbFactory factory, CancellationToken ct) => ValidateCoreAsync(deviceGuid, input, factory, ct, false);

    private async Task<AgentAdminSessionResponse?> ValidateCoreAsync(Guid deviceGuid, AgentAdminSessionRequest input,
        ICloneDeployDbFactory factory, CancellationToken ct, bool allowJob)
    {
        if (!Secret(input.Token)) return null;
        Session session;
        lock (gate)
        {
            Cleanup();
            if (!sessions.TryGetValue(Hash(input.Token), out session!) || session.DeviceGuid != deviceGuid ||
                session.WindowsSid != input.WindowsSid || session.WindowsSessionId != input.WindowsSessionId ||
                (!allowJob && session.JobCapability is not null)) return null;
        }
        await using var db = await factory.CreateContextAsync();
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId, ct);
        if (user is null || (await CapabilitiesAsync(db, user.Id, ct)).Length == 0 || user.SecurityStamp != session.SecurityStamp ||
            (user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()) || (!session.EntraAuthenticated && await MfaPolicy.RequiresWebAsync(db, user) && !user.TwoFactorEnabled))
        { Revoke(deviceGuid, input); return null; }
        lock (gate)
            if (!sessions.ContainsKey(Hash(input.Token)) || session.ExpiresAt <= clock.GetUtcNow()) return null;
        return new(input.Token, user.UserName ?? "Administrator", session.ExpiresAt, await CapabilitiesAsync(db, user.Id, ct));
    }

    public void Revoke(Guid deviceGuid, AgentAdminSessionRequest input)
    {
        if (!Secret(input.Token)) return;
        lock (gate)
            if (sessions.TryGetValue(Hash(input.Token), out var session) && session.DeviceGuid == deviceGuid &&
                session.WindowsSid == input.WindowsSid && session.WindowsSessionId == input.WindowsSessionId) sessions.Remove(Hash(input.Token));
    }
    public async Task<int?> AuthorizeToolAsync(Guid deviceGuid, AgentAdminSessionRequest input, string capability,
        ICloneDeployDbFactory factory, CancellationToken ct)
    {
        var authorized = await ValidateCoreAsync(deviceGuid, input, factory, ct, true);
        if (authorized is null || !authorized.Capabilities.Contains(capability, StringComparer.Ordinal)) return null;
        lock (gate)
            return sessions.TryGetValue(Hash(input.Token), out var session) && session.ExpiresAt > clock.GetUtcNow() &&
                (session.JobCapability is null || session.JobCapability == capability) ? session.UserId : null;
    }
    // A separate credential lets an admitted driver export finish without extending interactive access.
    public async Task<AgentAdminSessionResponse?> StartDriverJobAsync(Guid deviceGuid, AgentAdminSessionRequest input,
        ICloneDeployDbFactory factory, CancellationToken ct)
    {
        var authorized = await ValidateAsync(deviceGuid, input, factory, ct);
        if (authorized is null || !authorized.Capabilities.Contains("CreateDriverPack")) return null;
        lock (gate)
        {
            Cleanup();
            if (!sessions.TryGetValue(Hash(input.Token), out var session) || sessions.Count >= 4096) return null;
            var token = Encode(RandomNumberGenerator.GetBytes(32));
            var expires = clock.GetUtcNow().AddHours(24);
            sessions.Add(Hash(token), session with { ExpiresAt = expires, JobCapability = "CreateDriverPack" });
            return new(token, authorized.UserName, expires, ["CreateDriverPack"]);
        }
    }

    private void Cleanup()
    {
        var now = clock.GetUtcNow();
        foreach (var id in pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) pending.Remove(id);
        foreach (var key in sessions.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) sessions.Remove(key);
    }
}
