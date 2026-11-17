using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class RemoteAccessSettings
{
    public RemoteAccessMode DefaultMode { get; set; } = RemoteAccessMode.RequireApproval;
    public List<RemoteAccessRule> Rules { get; set; } = [];
}
public sealed class RemoteAccessRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public RemoteAccessMode Mode { get; set; } = RemoteAccessMode.RequireApproval;
    public List<int> DeviceIds { get; set; } = [];
    public List<int> GroupIds { get; set; } = [];
}
public sealed record RemoteAccessEffective(RemoteAccessMode Mode, string Rule, Guid? RuleId = null);
public sealed record RemoteAccessTarget(int Id, string Name);

public sealed class RemoteAccessRules(ICloneDeployDbFactory factory, IAuthorizationService authorization)
{
    public const string SettingsKey = "Agent.RemoteAccessRules";
    public static string Label(RemoteAccessMode mode) => mode switch
    {
        RemoteAccessMode.AllowUnattended => "Allow unattended",
        RemoteAccessMode.ApprovalWhenLoggedIn => "Require approval when logged in",
        _ => "Require approval"
    };
    private async Task RequireAdmin(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _)) throw new UnauthorizedAccessException();
    }
    public async Task<RemoteAccessSettings> GetAsync(ClaimsPrincipal user)
    {
        await RequireAdmin(user); await using var db = await factory.CreateContextAsync(); return await ReadAsync(db, default);
    }
    public async Task<(RemoteAccessTarget[] Devices, RemoteAccessTarget[] Groups)> TargetsAsync(ClaimsPrincipal user)
    {
        await RequireAdmin(user); await using var db = await factory.CreateContextAsync();
        return (await db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null).OrderBy(x => x.Name).Select(x => new RemoteAccessTarget(x.Id, x.Name)).ToArrayAsync(),
            await db.Set<DeviceGroup>().AsNoTracking().OrderBy(x => x.Name).Select(x => new RemoteAccessTarget(x.Id, x.Name)).ToArrayAsync());
    }
    public async Task<RemoteAccessEffective> EffectiveAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceRemoteView)).Succeeded) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync(); return await ResolveAsync(db, deviceId, ct);
    }
    public async Task SaveAsync(ClaimsPrincipal user, RemoteAccessSettings settings)
    {
        await RequireAdmin(user); Validate(settings);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947238, 1)");
        var devices = settings.Rules.SelectMany(x => x.DeviceIds).Distinct().ToArray();
        var groups = settings.Rules.SelectMany(x => x.GroupIds).Distinct().ToArray();
        if (await db.Devices.CountAsync(x => devices.Contains(x.Id) && x.ArchivedAtUtc == null) != devices.Length ||
            await db.Set<DeviceGroup>().CountAsync(x => groups.Contains(x.Id)) != groups.Length)
            throw new InvalidOperationException("A selected device or group is no longer available. Reload the rules.");
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == SettingsKey);
        if (setting is null) { setting = new AppSetting { Name = SettingsKey }; db.AppSettings.Add(setting); }
        setting.Value = JsonSerializer.Serialize(settings);
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = AuditType.Update, ObjectType = "RemoteAccessRules", ObjectName = "Remote access rules", ObjectJson = setting.Value, LoggedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public static void Validate(RemoteAccessSettings settings)
    {
        if (!System.Enum.IsDefined(settings.DefaultMode) || settings.Rules is null || settings.Rules.Count > 100)
            throw new InvalidOperationException("Invalid remote access settings.");
        if (settings.Rules.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 100 || !System.Enum.IsDefined(x.Mode) ||
            x.Priority is < 0 or > 100000 || x.DeviceIds is null || x.GroupIds is null || x.DeviceIds.Count + x.GroupIds.Count is < 1 or > 1000 || x.DeviceIds.Concat(x.GroupIds).Any(id => id <= 0)) ||
            settings.Rules.Select(x => x.Id).Distinct().Count() != settings.Rules.Count || settings.Rules.Select(x => x.Priority).Distinct().Count() != settings.Rules.Count)
            throw new InvalidOperationException("Each rule needs a name, unique priority, valid access mode, and at least one device or group.");
    }
    private static async Task<RemoteAccessSettings> ReadAsync(CloneDeployDbContext db, CancellationToken ct)
    {
        var json = await db.AppSettings.AsNoTracking().Where(x => x.Name == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        if (json is null) return new();
        // Invalid stored rules block access; never fall back to unattended access.
        var settings = JsonSerializer.Deserialize<RemoteAccessSettings>(json) ?? throw new InvalidOperationException("Remote access rules are unavailable.");
        Validate(settings); return settings;
    }
    public static RemoteAccessEffective Select(RemoteAccessSettings settings, int deviceId, IReadOnlyCollection<int> groups)
    {
        Validate(settings);
        var rule = settings.Rules.Where(x => x.Enabled && (x.DeviceIds.Contains(deviceId) || x.GroupIds.Any(groups.Contains))).OrderBy(x => x.Priority).FirstOrDefault();
        return rule is null ? new(settings.DefaultMode, "Global default") : new(rule.Mode, rule.Name, rule.Id);
    }
    public static async Task<RemoteAccessEffective> ResolveAsync(CloneDeployDbContext db, int deviceId, CancellationToken ct)
    {
        var settings = await ReadAsync(db, ct);
        var groups = await db.Set<DeviceGroupMembership>().Where(x => x.DeviceId == deviceId).Select(x => x.DeviceGroupId).ToArrayAsync(ct);
        return Select(settings, deviceId, groups);
    }
}
