using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ReplicationOptions(int ConcurrentSites = 2, int SharedFiles = 2, int BandwidthMiB = 0)
{
    public bool Valid => ConcurrentSites is >= 1 and <= 32 && SharedFiles is >= 1 and <= 16 && BandwidthMiB is >= 0 and <= 102400;
}

public sealed record SiteReplicationOptions(int BandwidthMiB = 0)
{
    public bool Valid => BandwidthMiB is >= 0 and <= 102400;
}

public sealed record ReplicationSettingsSnapshot(ReplicationOptions Global, IReadOnlyDictionary<Guid, SiteReplicationOptions> Sites)
{
    public SiteReplicationOptions ForSite(Guid site) => Sites.GetValueOrDefault(site) ?? new();
}

// Cached configuration only; transfer accounting is owned by the singleton coordinator.
public sealed class ReplicationSettingsStore(IServiceScopeFactory scopes)
{
    public const string Key = "Storage.Replication";
    public const string SitePrefix = Key + ".Site.";
    private readonly SemaphoreSlim gate = new(1, 1);
    private ReplicationSettingsSnapshot? cached;
    private long refreshed;
    public void Invalidate() => Interlocked.Exchange(ref refreshed, 0);

    public async Task<ReplicationSettingsSnapshot> ReadAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref cached) is { } value && Environment.TickCount64 - Interlocked.Read(ref refreshed) < 5000) return value;
        await gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref cached) is { } existing && Environment.TickCount64 - Interlocked.Read(ref refreshed) < 5000) return existing;
            using var scope = scopes.CreateScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
            var rows = await db.AppSettings.AsNoTracking().Where(x => x.Name == Key || x.Name.StartsWith(SitePrefix)).ToListAsync(ct);
            var global = Parse<ReplicationOptions>(rows.SingleOrDefault(x => x.Name == Key)?.Value) ?? new();
            if (!global.Valid) throw new InvalidDataException("Invalid replication settings.");
            var sites = new Dictionary<Guid, SiteReplicationOptions>();
            foreach (var row in rows.Where(x => x.Name.StartsWith(SitePrefix)))
            {
                if (!Guid.TryParseExact(row.Name[SitePrefix.Length..], "N", out var id)) continue;
                var settings = Parse<SiteReplicationOptions>(row.Value);
                if (settings is not { Valid: true }) throw new InvalidDataException("Invalid site replication settings.");
                sites[id] = settings;
            }
            value = new(global, sites);
            Volatile.Write(ref cached, value);
            Interlocked.Exchange(ref refreshed, Environment.TickCount64);
            return value;
        }
        finally { gate.Release(); }
    }

    internal static T? Parse<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json);
}

public sealed class ReplicationSettingsAdministration(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ReplicationSettingsStore settings, ILogger<ReplicationSettingsAdministration> logger)
{
    public async Task<ServiceResult<ReplicationSettingsSnapshot>> ReadAsync()
    {
        if (await AdminAsync() is null) return ServiceResult<ReplicationSettingsSnapshot>.Fail("StorageMount.Forbidden");
        return await ServiceOperations.RunAsync(async () => ServiceResult<ReplicationSettingsSnapshot>.Ok(await settings.ReadAsync(default)), logger);
    }

    public async Task<ServiceResult<bool>> SaveAsync(ReplicationOptions value, ReplicationOptions expected) =>
        await SaveCoreAsync(null, value, expected, value.Valid);

    public async Task<ServiceResult<bool>> SaveSiteAsync(Guid site, SiteReplicationOptions value, SiteReplicationOptions? expected) =>
        await SaveCoreAsync(site, value, expected, value.Valid && site != Guid.Empty);

    private async Task<ServiceResult<bool>> SaveCoreAsync<T>(Guid? site, T value, T? expected, bool valid) where T : class
    {
        var user = await AdminAsync();
        if (user is null) return ServiceResult<bool>.Fail("StorageMount.Forbidden");
        if (!valid) return ServiceResult<bool>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.ReplicationSettings.Use132Sites116SharedFilesAnd));
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903422)");
            if (site.HasValue && !await db.Set<SiteServiceRegistration>().AnyAsync(x => x.Id == site.Value))
                return ServiceResult<bool>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.ReplicationSettings.SiteServiceNoLongerExists));
            var key = site.HasValue ? ReplicationSettingsStore.SitePrefix + site.Value.ToString("N") : ReplicationSettingsStore.Key;
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == key);
            var previous = ReplicationSettingsStore.Parse<T>(row?.Value);
            if (!site.HasValue && previous is null) previous = (T)(object)new ReplicationOptions();
            if (!EqualityComparer<T?>.Default.Equals(previous, expected))
                return ServiceResult<bool>.Fail("Storage.Validation", UiText.Get(LocKeys.Ui.ReplicationSettings.ReplicationSettingsChangedRefreshBeforeSaving));
            var json = JsonSerializer.Serialize(value);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = key, Value = json }); else row.Value = json;
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Replication limits",
                ObjectJson = JsonSerializer.Serialize(new { Site = site, Previous = previous, Settings = value }) });
            await db.SaveChangesAsync(); await tx.CommitAsync(); settings.Invalidate();
            return ServiceResult<bool>.Ok(true);
        }, logger);
    }

    private async Task<ClaimsPrincipal?> AdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ? user : null;
    }
}
