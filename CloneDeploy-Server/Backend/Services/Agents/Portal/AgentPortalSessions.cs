using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// End-user identities only. These tokens are never accepted by web/admin authentication.
// Pending browser handoffs are short-lived; remembered sessions persist as token hashes in the database.
public sealed class AgentPortalSessions(TimeProvider clock)
{
    private sealed record Identity(int Source, Guid Object, Guid Domain);
    private sealed record Pending(Guid Device, AgentPortalSignInRequest Input, DateTimeOffset Expires)
    { public string? CodeHash; public Identity? User; }
    private readonly object gate = new();
    private readonly Dictionary<Guid, Pending> pending = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> attempts = [];
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Encode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Secret(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public AgentPortalSignInResponse Start(Guid device, AgentPortalSignInRequest input)
    {
        if (device == Guid.Empty || !ValidLocalUser(input.LocalUser) ||
            !Secret(input.State) || !Secret(input.CodeChallenge) || !Uri.TryCreate(input.RedirectUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port < 1024 || uri.AbsolutePath != "/callback" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Invalid portal sign-in request.");
        lock (gate)
        {
            Cleanup();
            foreach (var key in pending.Where(x => x.Value.Device == device && x.Value.Input.LocalUser == input.LocalUser).Select(x => x.Key).ToArray()) pending.Remove(key);
            if (pending.Count >= 1024) throw new InvalidOperationException("Too many sign-in requests.");
            var id = Guid.NewGuid(); var expires = clock.GetUtcNow().AddMinutes(5);
            pending.Add(id, new(device, input, expires));
            return new(id, $"/agent/portal-sign-in/{id:D}", expires);
        }
    }
    public Guid? PendingDevice(Guid id)
    {
        lock (gate) { Cleanup(); return pending.TryGetValue(id, out var value) && value.CodeHash is null ? value.Device : null; }
    }
    public async Task<string?> CompleteAsync(Guid id, string username, string password, ICloneDeployDbFactory factory, IAdPasswordValidator validator, CancellationToken ct)
    {
        var device = PendingDevice(id);
        if (device is null || string.IsNullOrWhiteSpace(username) || username.Length > 256 || string.IsNullOrEmpty(password) || password.Length > 1024) return null;
        var parts = username.Trim().Split('\\');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace)) return null;
        var domain = parts.Length == 2 ? parts[0] : null; var account = parts[^1].ToLowerInvariant();
        await using var db = await factory.CreateContextAsync();
        var sources = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).Include(x => x.SyncState)
            .Where(x => x.Provider == DirectoryProvider.ActiveDirectory && x.IsEnabled && x.SyncState != null && !x.ApplicationAccessOnly).ToListAsync(ct);
        if (sources.Count != 1 || sources[0].LdapSettings is null) return null;
        var source = sources[0];
        var candidates = db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == source.Id && x.Kind == DirectoryObjectKind.User && !x.IsMissing && !x.Disabled);
        candidates = account.Contains('@') ? candidates.Where(x => x.UserPrincipalName != null && x.UserPrincipalName.ToLower() == account)
            : candidates.Where(x => x.AccountName != null && x.AccountName.ToLower() == account);
        var matches = await candidates.Take(2).ToListAsync(ct);
        if (matches.Count != 1) return null;
        var user = matches[0]; var key = $"{source.Id}/{user.ObjectGuid:D}";
        lock (gate)
        {
            Cleanup();
            if (attempts.TryGetValue(key, out var old) && old.Count >= 5 || attempts.Count >= 4096) return null;
            attempts[key] = (old.Count + 1, old.Until > clock.GetUtcNow() ? old.Until : clock.GetUtcNow().AddMinutes(15));
        }
        if (!await validator.ValidateAsync(source, user, domain, password, ct)) return null;
        var identity = new Identity(source.Id, user.ObjectGuid, source.SyncState.DomainObjectGuid ?? source.SyncState.RootObjectGuid);
        var current = await ActiveAsync(db, identity, ct);
        // Recheck source configuration and object after the network bind.
        var currentSource = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).SingleOrDefaultAsync(x => x.Id == source.Id, ct);
        if (current is null || current.DistinguishedName != user.DistinguishedName || current.ObjectSid != user.ObjectSid ||
            currentSource?.LdapSettings?.Host != source.LdapSettings!.Host || currentSource?.LdapSettings?.Port != source.LdapSettings.Port) return null;
        lock (gate)
        {
            Cleanup();
            if (!pending.TryGetValue(id, out var request) || request.CodeHash is not null) return null;
            var code = Encode(RandomNumberGenerator.GetBytes(32));
            request.User = identity; request.CodeHash = Hash(code); attempts.Remove(key);
            return request.Input.RedirectUri + "?code=" + code + "&state=" + request.Input.State;
        }
    }
    // Invoked only by the validated OIDC ticket event, never from client-supplied claims.
    public async Task<string?> CompleteEntraAsync(Guid id, System.Security.Claims.ClaimsPrincipal principal, ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (PendingDevice(id) is null) return null;
        await using var db = await factory.CreateContextAsync();
        var settings = await db.Set<EntraConnection>().AsNoTracking().SingleOrDefaultAsync(ct);
        var key = Auth.EntraAuthentication.IdentityKey(settings, principal);
        if (key is null || settings?.CertificateEncrypted is null) return null;
        var oid = Guid.Parse(key.Split('/')[1]);
        var matches = await db.DirectoryObjects.AsNoTracking().Where(x => x.ObjectGuid == oid &&
            x.DirectorySource.Provider == DirectoryProvider.EntraId && x.DirectorySource.IsEnabled &&
            x.DirectorySource.SyncState.RootObjectGuid == settings.TenantId &&
            x.Kind == DirectoryObjectKind.User && !x.Disabled && !x.IsMissing).Take(2).ToListAsync(ct);
        if (matches.Count != 1) return null;
        var identity = new Identity(matches[0].DirectorySourceId, oid, settings.TenantId);
        if (await ActiveAsync(db, identity, ct) is null) return null;
        lock (gate)
        {
            Cleanup();
            if (!pending.TryGetValue(id, out var request) || request.CodeHash is not null) return null;
            var code = Encode(RandomNumberGenerator.GetBytes(32));
            request.User = identity; request.CodeHash = Hash(code);
            return request.Input.RedirectUri + "?code=" + code + "&state=" + request.Input.State;
        }
    }
    internal static bool ValidLocalUser(string? value)
    {
        if (value is not { Length: > 0 and <= 100 }) return false;
        if (value.StartsWith("macos:", StringComparison.Ordinal))
            return uint.TryParse(value[6..], out var uid) && uid != 0 && value == $"macos:{uid}";
        // Canonical Windows account SID, bound to the enrolled device by the session.
        if (!value.StartsWith("windows:S-1-", StringComparison.Ordinal)) return false;
        var parts = value[12..].Split('-');
        return parts.Length is >= 2 and <= 16 && ulong.TryParse(parts[0], out var authority) && authority <= 0xffffffffffff &&
            parts[0] == authority.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            parts.Skip(1).All(x => uint.TryParse(x, out var n) && x == n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public async Task<AgentPortalUserSession?> ExchangeAsync(Guid device, AgentPortalCodeRequest input, ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Secret(input.Code) || !Secret(input.CodeVerifier)) return null;
        Pending request;
        lock (gate)
        {
            Cleanup();
            if (!pending.TryGetValue(input.RequestId, out request!) || request.Device != device || request.Input.LocalUser != input.LocalUser ||
                request.User is null || request.CodeHash is null || !Equal(request.CodeHash, Hash(input.Code)) || !Equal(request.Input.CodeChallenge, Hash(input.CodeVerifier))) return null;
            pending.Remove(input.RequestId);
        }
        await using var db = await factory.CreateContextAsync();
        var user = await ActiveAsync(db, request.User, ct);
        if (user is null) return null;
        var token = Encode(RandomNumberGenerator.GetBytes(32));
        var days = Math.Clamp((await AgentFeatureSettings.FeaturesAsync(db, ct)).PortalSessionDays, 1, 365);
        var expires = clock.GetUtcNow().AddDays(days);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize replacements for this device across concurrent server instances.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({device.ToString()}, 0))", ct);
        await db.Set<AgentPortalSession>().Where(x => x.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime ||
            x.DeviceGuid == device && x.LocalUser == input.LocalUser).ExecuteDeleteAsync(ct);
        db.Add(new AgentPortalSession { TokenHash = Hash(token), DeviceGuid = device, LocalUser = input.LocalUser,
            DirectorySourceId = request.User.Source, ObjectGuid = request.User.Object, DomainGuid = request.User.Domain,
            ExpiresAtUtc = expires.UtcDateTime });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(token, PublicUser(user), expires) { UserSid = user.ObjectSid };
    }
    public async Task<AgentPortalUserSession?> ValidateAsync(Guid device, AgentPortalSessionRequest input, ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Secret(input.Token)) return null;
        await using var db = await factory.CreateContextAsync();
        var hash = Hash(input.Token);
        var session = await db.Set<AgentPortalSession>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash &&
            x.DeviceGuid == device && x.LocalUser == input.LocalUser && x.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime, ct);
        if (session is null) return null;
        var user = await ActiveAsync(db, new(session.DirectorySourceId, session.ObjectGuid, session.DomainGuid), ct);
        if (user is null) { await RevokeAsync(device, input, factory, ct); return null; }
        if (!await db.Set<AgentPortalSession>().AnyAsync(x => x.TokenHash == hash && x.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime, ct)) return null;
        return new(input.Token, PublicUser(user), new DateTimeOffset(session.ExpiresAtUtc, TimeSpan.Zero)) { UserSid = user.ObjectSid };
    }
    public async Task RevokeAsync(Guid device, AgentPortalSessionRequest input, ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Secret(input.Token)) return;
        await using var db = await factory.CreateContextAsync();
        var hash = Hash(input.Token);
        await db.Set<AgentPortalSession>().Where(x => x.TokenHash == hash && x.DeviceGuid == device && x.LocalUser == input.LocalUser).ExecuteDeleteAsync(ct);
    }
    // Persist the authenticating identity, then resolve it again on every validation so disabling
    // either directory identity revokes hybrid sessions. Never create an application user/login.
    private static async Task<DirectoryObject?> ActiveAsync(CloneDeployDbContext db, Identity identity, CancellationToken ct)
    {
        var user = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
        .Where(x => x.DirectorySourceId == identity.Source && x.ObjectGuid == identity.Object && x.Kind == DirectoryObjectKind.User && !x.Disabled && !x.IsMissing &&
            x.DirectorySource.IsEnabled &&
            (x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory ||
             x.DirectorySource.Provider == DirectoryProvider.EntraId && db.Set<EntraConnection>().Any(e => e.SsoEnabled && e.CertificateEncrypted != null && e.TenantId == identity.Domain)) &&
            (x.DirectorySource.SyncState.DomainObjectGuid ?? x.DirectorySource.SyncState.RootObjectGuid) == identity.Domain).SingleOrDefaultAsync(ct);
        if (user is null) return null;
        if (!user.DirectorySource.ApplicationAccessOnly) return user;
        if (user.DirectorySource.Provider != DirectoryProvider.EntraId ||
            user.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) != true) return null;
        var matches = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => x.Kind == DirectoryObjectKind.User && x.ObjectSid == user.ObjectSid && !x.IsMissing &&
                x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly &&
                x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory && x.DirectorySource.SyncState != null)
            .Take(2).ToListAsync(ct);
        // Reject duplicate cloud SID mappings as well as duplicate LDAP identities.
        var cloudMatches = await db.DirectoryObjects.CountAsync(x => x.DirectorySourceId == user.DirectorySourceId &&
            x.Kind == DirectoryObjectKind.User && !x.IsMissing && x.ObjectSid == user.ObjectSid, ct);
        return cloudMatches == 1 && matches.Count == 1 && !matches[0].Disabled ? matches[0] : null;
    }
    private static AgentDirectoryUser PublicUser(DirectoryObject value) => new(value.DirectorySourceId, value.ObjectGuid, value.Name, value.UserPrincipalName, value.DirectorySource.Name);
    private void Cleanup()
    {
        var now = clock.GetUtcNow();
        foreach (var key in pending.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) pending.Remove(key);
        foreach (var key in attempts.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) attempts.Remove(key);
    }
}
