using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentFeatureConfiguration
{
    public string Version { get; init; } = "";
    public AgentFeatures Features { get; init; } = new();
    public int? WinGetAcceptedById { get; init; }
    public string? WinGetAcceptedBy { get; init; }
    public DateTime? WinGetAcceptedAtUtc { get; init; }
    public string? WinGetAcknowledgment { get; init; }
}

public static class AgentFeatureSettings
{
    public static bool UsesWinGet(CompiledPolicyActionDto action) =>
        action.Type == CloneDeploy_Agent.Contracts.ActionType.Software && !(action.Settings.TryGetProperty("Uninstall", out var uninstall) && uninstall.ValueKind == JsonValueKind.True) && action.Settings.TryGetProperty("Type", out var type) && type.GetInt32() == (int)CloneDeploy_Agent.Contracts.SoftwareActionType.WinGet
        || action.Condition is { } condition && UsesWinGet(condition);
    public static bool UsesWinGet(CompiledPolicyContentDto policy) => policy.Actions.Any(UsesWinGet) || policy.Condition is { } condition && UsesWinGet(condition);
    public const string Key = "Agent.Features";
    public const string DirectoryRequired = "Configure and enable an LDAP or Entra directory in Directory Settings before enabling Help Desk or Self Service. These features require directory user identities.";
    internal static Task<bool> HasPortalDirectoryAsync(CloneDeployDbContext db) =>
        db.DirectorySources.AnyAsync(source => source.IsEnabled &&
            (source.Provider == DirectoryProvider.ActiveDirectory && source.LdapSettings != null &&
             source.LdapSettings.Host.Trim() != "" && source.LdapSettings.ManagedCredentialId > 0 ||
             source.Provider == DirectoryProvider.EntraId && db.EntraConnections.Any(connection =>
                 connection.TenantId != Guid.Empty && connection.ClientId != Guid.Empty &&
                 connection.CertificateEncrypted != null && connection.CertificateEncrypted != "")));
    public const string WinGetDisabled = "WinGet integration is disabled. Enable it and acknowledge unattended deployments in Agent Settings → Features.";
    public const string Acknowledgment = "Enable unattended WinGet deployments. CloneDeploy automatically accepts applicable source and package agreements during deployment. Your organization is responsible for reviewing those terms and ensuring it has the required software licenses.";
    public static async Task<AgentFeatureConfiguration> ReadAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var json = await db.AppSettings.Where(x => x.Name == Key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return json is null ? new() : JsonSerializer.Deserialize<AgentFeatureConfiguration>(json) ?? new();
    }
    public static async Task<AgentFeatures> FeaturesAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var value = await ReadAsync(db, ct);
        return value.Features with { WinGetEnabled = value.Features.WinGetEnabled && value.WinGetAcceptedAtUtc != null && value.WinGetAcknowledgment == Acknowledgment };
    }
}

public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult> CheckPortalDirectoryAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            return await AgentFeatureSettings.HasPortalDirectoryAsync(db)
                ? ServiceResult.Ok()
                : ServiceResult.Fail("ServerSettings.Validation", AgentFeatureSettings.DirectoryRequired);
        }, logger);
    }
    public async Task<ServiceResult<AgentFeatureConfiguration>> ReadFeaturesAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<AgentFeatureConfiguration>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            return ServiceResult<AgentFeatureConfiguration>.Ok(await AgentFeatureSettings.ReadAsync(db));
        }, logger);
    }
    public async Task<ServiceResult<AgentFeatureConfiguration>> SaveFeaturesAsync(AgentFeatures features, string expectedVersion, bool acknowledgeWinGet)
    {
        if (features.PortalSessionDays is < 1 or > 365) return ServiceResult<AgentFeatureConfiguration>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.AgentFeatureSettings.PortalSignInDurationMustBeBetween1And));
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<AgentFeatureConfiguration>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947236, 0)");
            var previous = await AgentFeatureSettings.ReadAsync(db);
            if (previous.Version != expectedVersion) return ServiceResult<AgentFeatureConfiguration>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.AgentFeatureSettings.FeatureSettingsChangedReloadBeforeSaving));
            if ((features.HelpDeskEnabled || features.SelfServiceEnabled) && !await AgentFeatureSettings.HasPortalDirectoryAsync(db))
                return ServiceResult<AgentFeatureConfiguration>.Fail("ServerSettings.Validation", AgentFeatureSettings.DirectoryRequired);
            var needsAcceptance = features.WinGetEnabled && (!previous.Features.WinGetEnabled || previous.WinGetAcknowledgment != AgentFeatureSettings.Acknowledgment || previous.WinGetAcceptedAtUtc == null);
            if (needsAcceptance && !acknowledgeWinGet) return ServiceResult<AgentFeatureConfiguration>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.AgentFeatureSettings.AcknowledgeUnattendedWinGetDeploymentsBeforeEnablingWinGet));
            var value = previous with { Version = Guid.NewGuid().ToString("N"), Features = features };
            if (needsAcceptance) value = value with { WinGetAcceptedById = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                WinGetAcceptedBy = user.Identity?.Name ?? "Administrator", WinGetAcceptedAtUtc = DateTime.UtcNow, WinGetAcknowledgment = AgentFeatureSettings.Acknowledgment };
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == AgentFeatureSettings.Key);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = AgentFeatureSettings.Key, Value = JsonSerializer.Serialize(value) });
            else row.Value = JsonSerializer.Serialize(value);
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Agent features",
                ObjectJson = JsonSerializer.Serialize(new { Previous = previous.Features, Settings = value, AcceptedWinGet = needsAcceptance }) });
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return ServiceResult<AgentFeatureConfiguration>.Ok(value);
        }, logger);
    }
}
