using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record SiteConfigurationUpdate(string ListenAddress, bool ProxyEnabled, bool Approve,
    SiteProxyAccess Access, int EnrollmentLimit, SiteReplicationOptions Replication,
    SiteReplicationOptions? ExpectedReplication, bool WorkerEnabled);

public sealed record SiteServiceDetails(Guid Id, string Name, SiteRegistrationState State, int Revision, string BackendUrl,
    string PublicUrl, string ListenAddress, bool ProxyEnabled, string? Hostname, string? Addresses, string? KeyFingerprint,
    DateTime TokenExpiresAtUtc, DateTime? LastContactAtUtc, DateTime? CertificateExpiresAtUtc, int AppliedRevision, bool ProxyRunning, string? LastError)
{
    public int WindowsWorkerRoles { get; init; }
    public bool IsWindowsWorker { get; init; }
    public string? HostOs { get; init; }
    public string BackendAddress { get; init; } = "";
    public string BootSettingsJson { get; init; } = "{}";
    public string? BootStatusJson { get; init; }
    public SiteBootSettings Boot => JsonSerializer.Deserialize<SiteBootSettings>(BootSettingsJson) ?? new();
    public SiteBootStatus? BootStatus => BootStatusJson is null ? null : JsonSerializer.Deserialize<SiteBootStatus>(BootStatusJson);
}

public sealed class SiteServiceAdministration(ICloneDeployDbFactory factory, ISecretProtector secrets,
    IAuthorizationService authorization, TimeProvider clock, ServerUrlSettingsStore urls)
{
    public WindowsWorkerPolicy WorkerPolicy { get; init; } = new();
    public async Task<bool> CanCreateWorkerAsync(ClaimsPrincipal user)
    {
        await Admin(user);
        if (!WorkerPolicy.RemoteAllowed) return false;
        await using var db = await factory.CreateContextAsync();
        return !await db.SiteServices.AnyAsync(x => x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked);
    }
    public async Task<string> PublicUrlAsync(ClaimsPrincipal user)
    { await Admin(user); return await urls.RequireServiceUrlAsync(); }

    private static string ProxyRateKey(Guid id) => $"SiteService.{id:D}.AuthenticationRequestsPerMinute";

    internal static async Task<int> ReadProxyRateAsync(CloneDeployDbContext db, Guid id, CancellationToken ct = default)
    {
        var key = ProxyRateKey(id);
        var value = await db.AppSettings.Where(x => x.Name == key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (value is null) return 0;
        if (!int.TryParse(value, out var limit) || limit is < 0 or > 1000000)
            throw new InvalidDataException("Invalid site authentication rate limit.");
        return limit;
    }

    public async Task<int> GetProxyRateLimitAsync(ClaimsPrincipal user, Guid id)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        return await ReadProxyRateAsync(db, id);
    }

    public async Task SaveProxyRateLimitAsync(ClaimsPrincipal user, Guid id, int revision, int limit)
    {
        await Admin(user);
        if (limit is < 0 or > 1000000) throw new InvalidDataException("Enter a rate limit between 0 and 1000000.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleAsync(x => x.Id == id); CheckRevision(row, revision);
        if (row.State != SiteRegistrationState.Approved) throw new InvalidDataException("Approve the site before configuring its rate limit.");
        var key = ProxyRateKey(id);
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == key);
        if (setting is null) { setting = new AppSetting { Name = key }; db.AppSettings.Add(setting); }
        setting.Value = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.Revision++;
        Audit(db, user, row, $"Authentication requests per minute per endpoint: {limit} (0 disables limit)");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    internal static async Task<SiteProxyAccess> ReadProxyAccessAsync(CloneDeployDbContext db, Guid id, CancellationToken ct = default)
    {
        if (await db.SiteServices.AnyAsync(x => x.Id == id && x.IsWindowsWorker, ct))
            return new(false, 0) { AgentsEnabled = false, EnrollmentEnabled = false, PortalEnabled = false, AgentAdminEnabled = false, StorageEnabled = false, MulticastEnabled = false };
        var key = $"SiteService.{id:D}.ProxyAccess";
        var value = await db.AppSettings.Where(x => x.Name == key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        // Preserve existing imaging access and its previous limit during upgrades.
        var access = value is null ? new SiteProxyAccess(true, await ReadProxyRateAsync(db, id, ct))
            : JsonSerializer.Deserialize<SiteProxyAccess>(value) ?? throw new InvalidDataException("Invalid site proxy settings.");
        if (access.ImagingLoginRequestsPerMinute is < 0 or > 1000000) throw new InvalidDataException("Invalid imaging login rate limit.");
        access.Validate();
        // Storage is determined by assigned providers, never by the saved capability flag.
        // This also ignores an old StorageEnabled=false without requiring a configuration resave.
        return access with { StorageEnabled = await db.StorageProviders.AnyAsync(x => x.SiteServiceId == id && x.IsEnabled && !x.IsDeleted, ct) };
    }

    public async Task<SiteProxyAccess> GetProxyAccessAsync(ClaimsPrincipal user, Guid id)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        return await ReadProxyAccessAsync(db, id);
    }

    public async Task SaveProxyAccessAsync(ClaimsPrincipal user, Guid id, int revision, int enrollmentLimit, SiteProxyAccess access)
    {
        await Admin(user);
        access.Validate();
        if (enrollmentLimit is < 0 or > 1000000 || access.ImagingLoginRequestsPerMinute is < 0 or > 1000000)
            throw new InvalidDataException("Enter rate limits between 0 and 1000000.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleAsync(x => x.Id == id); CheckRevision(row, revision);
        if (row.State != SiteRegistrationState.Approved) throw new InvalidDataException("Approve the site before configuring proxy access.");
        if (row.IsWindowsWorker) throw new InvalidDataException("Windows Workers cannot enable site capabilities.");
        foreach (var entry in new[] { (ProxyRateKey(id), enrollmentLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ($"SiteService.{id:D}.ProxyAccess", JsonSerializer.Serialize(access)) })
        {
            var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == entry.Item1);
            if (setting is null) { setting = new AppSetting { Name = entry.Item1 }; db.AppSettings.Add(setting); }
            setting.Value = entry.Item2;
        }
        row.Revision++;
        Audit(db, user, row, $"Capabilities: agents={access.AgentsEnabled}, enrollment={access.EnrollmentEnabled}, portal={access.PortalEnabled}, admin={access.AgentAdminEnabled}, admin networks={access.AdminNetworks}, storage={access.StorageEnabled}, multicast={access.MulticastEnabled}; proxy imaging: {access.ImagingEnabled}; enrollment requests/min: {enrollmentLimit}; imaging login requests/min: {access.ImagingLoginRequestsPerMinute}");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<SiteServiceDetails>> ListAsync(ClaimsPrincipal user)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        return await db.SiteServices.AsNoTracking().OrderBy(x => x.Name).Select(x => new SiteServiceDetails(x.Id, x.Name,
            x.State, x.Revision, x.BackendUrl, x.PublicUrl, x.ListenAddress, x.ProxyEnabled, x.Hostname, x.Addresses, x.KeyFingerprint,
            x.TokenExpiresAtUtc, x.LastContactAtUtc, x.CertificateExpiresAtUtc, x.AppliedRevision, x.ProxyRunning, x.LastError) { IsWindowsWorker = x.IsWindowsWorker, WindowsWorkerRoles = x.WindowsWorkerRoles, HostOs = x.HostOs, BackendAddress = x.BackendAddress, BootSettingsJson = x.BootSettingsJson, BootStatusJson = x.BootStatusJson }).ToListAsync();
    }

    public async Task<SiteServiceDetails?> GetByIdAsync(ClaimsPrincipal user, Guid id)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        return await db.SiteServices.AsNoTracking().Where(x => x.Id == id).Select(x => new SiteServiceDetails(x.Id, x.Name,
            x.State, x.Revision, x.BackendUrl, x.PublicUrl, x.ListenAddress, x.ProxyEnabled, x.Hostname, x.Addresses, x.KeyFingerprint,
            x.TokenExpiresAtUtc, x.LastContactAtUtc, x.CertificateExpiresAtUtc, x.AppliedRevision, x.ProxyRunning, x.LastError)
            { IsWindowsWorker = x.IsWindowsWorker, WindowsWorkerRoles = x.WindowsWorkerRoles, HostOs = x.HostOs, BackendAddress = x.BackendAddress, BootSettingsJson = x.BootSettingsJson, BootStatusJson = x.BootStatusJson }).SingleOrDefaultAsync();
    }

    public async Task SaveWindowsWorkerAsync(ClaimsPrincipal user, Guid id, int revision, WindowsWorkerRoles roles)
    {
        await Admin(user);
        WorkerPolicy.RequireRemote();
        if ((int)roles is not (0 or 7)) throw new InvalidDataException("Windows Worker can only be enabled or disabled as a whole.");
        await using var db = await factory.CreateContextAsync(); await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleAsync(x => x.Id == id); CheckRevision(row, revision);
        if (!row.IsWindowsWorker || row.ProxyEnabled || row.State != SiteRegistrationState.Approved || roles != 0 && row.HostOs != "Windows")
            throw new InvalidDataException("Approve the service and wait for its Windows OS heartbeat before enabling worker capabilities.");
        if (roles != 0 && await db.SiteServices.AnyAsync(x => x.Id != id && x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked))
            throw new InvalidDataException("Only one Windows Worker registration is allowed. Revoke the previous registration before replacing it.");
        row.WindowsWorkerRoles = (int)roles; row.Revision++;
        var jobs = await db.Set<WindowsWorkerJob>().Where(x => x.SiteId == id && (x.State == "Queued" || x.State == "Running")).ToListAsync();
        foreach (var job in jobs.Where(x => ((int)WindowsWorkFiles.Role(x.Kind) & (int)roles) == 0))
        { job.State = "Cancelled"; job.RequestEncrypted = ""; job.Error = "Windows worker capability was disabled."; }
        Audit(db, user, row, "Windows worker capabilities: " + roles); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<SiteEnrollmentPackage> CreateAsync(ClaimsPrincipal user, string name)
        => await CreateRegistrationAsync(user, name, false);

    public Task<SiteEnrollmentPackage> CreateWorkerAsync(ClaimsPrincipal user, string name)
        => CreateRegistrationAsync(user, name, true);

    private async Task<SiteEnrollmentPackage> CreateRegistrationAsync(ClaimsPrincipal user, string name, bool worker)
    {
        await Admin(user);
        if (worker) WorkerPolicy.RequireRemote();
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw new InvalidDataException("Enter a name of up to 100 characters.");
        var address = await urls.RequireMainServerAddressAsync();
        var publicUrl = Origin(await urls.RequireServiceUrlAsync());
        var backendUrl = publicUrl;
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        if (worker && await db.SiteServices.AnyAsync(x => x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked))
            throw new InvalidDataException("Only one Windows Worker registration is allowed. Revoke the previous registration before replacing it.");
        var issuer = await db.AppCertificates.SingleOrDefaultAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked)
            ?? throw new InvalidDataException("Create a valid CloneDeploy certificate authority first.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (issuer.NotBeforeUtc > now || issuer.NotAfterUtc < now.AddDays(2)) throw new InvalidDataException("The certificate authority is not valid for enrollment.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var row = new SiteServiceRegistration { IsWindowsWorker = worker, Id = Guid.NewGuid(), Name = name, BackendUrl = backendUrl, PublicUrl = publicUrl, BackendAddress = address.ToString(),
            IssuerId = issuer.Id, CreatedAtUtc = now, TokenExpiresAtUtc = now.AddHours(24), TokenHash = Hash(token), Revision = 1 };
        db.SiteServices.Add(row); Audit(db, user, row, "Created enrollment"); await db.SaveChangesAsync(); await tx.CommitAsync();
        using var root = WebCertificateService.Load(issuer, secrets);
        return new(1, row.Id, backendUrl, root.ExportCertificatePem(), token, row.TokenExpiresAtUtc) { IsWindowsWorker = row.IsWindowsWorker, BackendAddress = row.BackendAddress };
    }

    public async Task<SiteEnrollmentPackage> ExportAsync(ClaimsPrincipal user, Guid id, int revision)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.Include(x => x.Issuer).SingleAsync(x => x.Id == id);
        CheckRevision(row, revision);
        if (row.State != SiteRegistrationState.Created) throw new InvalidDataException("This setup file has already been redeemed. Create a new registration for another installation.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        row.TokenHash = Hash(token); row.TokenExpiresAtUtc = clock.GetUtcNow().AddHours(24).UtcDateTime; row.Revision++;
        using var root = WebCertificateService.Load(row.Issuer, secrets);
        if (row.Issuer.IsRevoked || row.Issuer.NotBeforeUtc > clock.GetUtcNow().UtcDateTime || row.Issuer.NotAfterUtc < clock.GetUtcNow().AddDays(2).UtcDateTime)
            throw new InvalidDataException("The certificate authority is unavailable.");
        Audit(db, user, row, "Replaced enrollment token"); await db.SaveChangesAsync(); await tx.CommitAsync();
        return new(1, row.Id, row.BackendUrl, root.ExportCertificatePem(), token, row.TokenExpiresAtUtc) { IsWindowsWorker = row.IsWindowsWorker, BackendAddress = row.BackendAddress };
    }

    public async Task SaveAsync(ClaimsPrincipal user, Guid id, int revision, string listenAddress, bool proxyEnabled, bool approve)
    {
        await Admin(user);
        if (!IPAddress.TryParse(listenAddress, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            throw new InvalidDataException("Select a specific IP assigned to the SiteService machine.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.Include(x => x.Issuer).SingleAsync(x => x.Id == id);
        CheckRevision(row, revision);
        if (approve ? row.State != SiteRegistrationState.Pending : row.State != SiteRegistrationState.Approved)
            throw new InvalidDataException("Refresh the service before changing its approval or configuration.");
        if (row.IsWindowsWorker)
        {
            WorkerPolicy.RequireRemote();
            if (proxyEnabled || row.HostOs != "Windows") throw new InvalidDataException("Windows Worker requires Windows and cannot enable a proxy.");
        }
        if (!row.IsWindowsWorker && IPAddress.TryParse(row.BackendAddress, out var backendIp) && backendIp.Equals(ip))
            throw new InvalidDataException("The main server IP and SiteService listening IP must differ when sharing the public port.");
        // Site listeners are mandatory; only Windows Workers have no public listener.
        row.ListenAddress = ip.ToString(); row.ProxyEnabled = !row.IsWindowsWorker;
        if (approve)
        {
            if (row.Issuer.IsRevoked) throw new InvalidDataException("The issuing CA has been revoked.");
            using var root = WebCertificateService.Load(row.Issuer, secrets);
            SiteServiceCertificates.Issue(row, root, clock.GetUtcNow());
            row.State = SiteRegistrationState.Approved;
        }
        row.Revision++;
        Audit(db, user, row, approve ? "Approved" : "Changed configuration");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<SiteReplicationOptions?> ReadReplicationAsync(ClaimsPrincipal user, Guid id)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        var key = ReplicationSettingsStore.SitePrefix + id.ToString("N");
        var json = await db.AppSettings.Where(x => x.Name == key).Select(x => x.Value).SingleOrDefaultAsync();
        return ReplicationSettingsStore.Parse<SiteReplicationOptions>(json);
    }

    public async Task SaveConfigurationAsync(ClaimsPrincipal user, Guid id, int revision, SiteConfigurationUpdate value)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.Include(x => x.Issuer).SingleAsync(x => x.Id == id);
        CheckRevision(row, revision);
        if (value.Approve ? row.State != SiteRegistrationState.Pending : row.State != SiteRegistrationState.Approved)
            throw new InvalidDataException("Refresh the service before changing its approval or configuration.");
        if (!IPAddress.TryParse(value.ListenAddress, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            throw new InvalidDataException("Select a specific IP assigned to the SiteService machine.");
        if (row.IsWindowsWorker)
        {
            WorkerPolicy.RequireRemote();
            if (value.ProxyEnabled || row.HostOs != "Windows")
                throw new InvalidDataException("Windows Worker requires Windows and cannot enable a proxy.");
            if (value.WorkerEnabled && await db.SiteServices.AnyAsync(x => x.Id != id && x.IsWindowsWorker && x.State != SiteRegistrationState.Revoked))
                throw new InvalidDataException("Only one Windows Worker registration is allowed.");
            row.WindowsWorkerRoles = value.WorkerEnabled ? 7 : 0;
            if (!value.WorkerEnabled)
            {
                var jobs = await db.Set<WindowsWorkerJob>().Where(x => x.SiteId == id && (x.State == "Queued" || x.State == "Running")).ToListAsync();
                foreach (var job in jobs)
                { job.State = "Cancelled"; job.RequestEncrypted = ""; job.Error = "Windows worker capability was disabled."; }
            }
        }
        else
        {
            value.Access.Validate();
            if (value.EnrollmentLimit is < 0 or > 1000000 || value.Access.ImagingLoginRequestsPerMinute is < 0 or > 1000000)
                throw new InvalidDataException("Enter rate limits between 0 and 1000000.");
            if (!value.Replication.Valid) throw new InvalidDataException("Use a replication bandwidth between 0 and 102400 MiB/s.");
            if (IPAddress.TryParse(row.BackendAddress, out var backendIp) && backendIp.Equals(ip))
                throw new InvalidDataException("The main server IP and SiteService listening IP must differ when sharing the public port.");
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903422)");
            var replicationKey = ReplicationSettingsStore.SitePrefix + id.ToString("N");
            var previous = await db.AppSettings.Where(x => x.Name == replicationKey).Select(x => x.Value).SingleOrDefaultAsync();
            if (ReplicationSettingsStore.Parse<SiteReplicationOptions>(previous) != value.ExpectedReplication)
                throw new InvalidDataException("Replication settings changed. Refresh before saving.");
            foreach (var (key, text) in new[] {
                (ProxyRateKey(id), value.EnrollmentLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ($"SiteService.{id:D}.ProxyAccess", JsonSerializer.Serialize(value.Access)),
                (replicationKey, JsonSerializer.Serialize(value.Replication)) })
            {
                var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == key);
                if (setting is null) db.AppSettings.Add(new AppSetting { Name = key, Value = text }); else setting.Value = text;
            }
        }
        value = value with { ProxyEnabled = !row.IsWindowsWorker };
        row.ListenAddress = ip.ToString(); row.ProxyEnabled = value.ProxyEnabled;
        if (value.Approve)
        {
            if (row.Issuer.IsRevoked) throw new InvalidDataException("The issuing CA has been revoked.");
            using var root = WebCertificateService.Load(row.Issuer, secrets);
            SiteServiceCertificates.Issue(row, root, clock.GetUtcNow());
            row.State = SiteRegistrationState.Approved;
        }
        row.Revision++;
        Audit(db, user, row, "Saved configuration: " + JsonSerializer.Serialize(value));
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task SaveBootAsync(ClaimsPrincipal user, Guid id, int revision, SiteBootSettings settings)
    {
        await Admin(user); settings.Validate();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleAsync(x => x.Id == id); CheckRevision(row, revision);
        if (row.State != SiteRegistrationState.Approved) throw new InvalidDataException("Approve the Site Service before enabling boot roles.");
        if (row.IsWindowsWorker) throw new InvalidDataException("Windows Workers cannot enable boot services.");
        row.BootSettingsJson = JsonSerializer.Serialize(settings); row.Revision++;
        Audit(db, user, row, "Changed ProxyDHCP/TFTP configuration");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task RevokeAsync(ClaimsPrincipal user, Guid id, int revision)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleAsync(x => x.Id == id); CheckRevision(row, revision);
        row.State = SiteRegistrationState.Revoked; row.TokenHash = null; row.ProxyEnabled = false; row.Revision++;
        Audit(db, user, row, "Revoked"); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task DeleteAsync(ClaimsPrincipal user, Guid id, int revision)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(db);
        var row = await db.SiteServices.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidDataException("The service no longer exists. Refresh the list.");
        CheckRevision(row, revision);
        if (row.State != SiteRegistrationState.Revoked)
            throw new InvalidDataException("Revoke the service before deleting it.");
        if (await db.StorageProviders.AnyAsync(x => x.SiteServiceId == id))
            throw new InvalidDataException("Remove this service's storage providers before deleting it.");
        // Retain the revoked identity and historical references, as with deleted storage providers.
        row.IsDeleted = true; row.Revision++;
        Audit(db, user, row, "Deleted");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    private async Task Admin(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _)) throw new UnauthorizedAccessException();
    }
    internal static Task Lock(CloneDeployDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903429)", ct);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void CheckRevision(SiteServiceRegistration row, int revision)
    { if (row.Revision != revision) throw new InvalidDataException("The service changed. Refresh before saving."); }
    private static string Origin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            Uri.CheckHostName(uri.Host.Trim('[', ']')) == UriHostNameType.Unknown)
            throw new InvalidDataException("Use an HTTPS address without a path, credentials, query or fragment.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, SiteServiceRegistration row, string action) =>
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update, ObjectType = nameof(SiteServiceRegistration),
            ObjectName = row.Name, ObjectJson = JsonSerializer.Serialize(new { row.Id, Action = action, row.Revision, row.ProxyEnabled, row.ListenAddress }) });
}
