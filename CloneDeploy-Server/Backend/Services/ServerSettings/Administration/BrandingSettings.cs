using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public static class BrandingSettings
{
    public const string Key = "OrganizationBranding";
    public static async Task<AgentBranding> ReadAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var json = await db.AppSettings.Where(x => x.Name == Key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return json is null ? new() : JsonSerializer.Deserialize<AgentBranding>(json) ?? new();
    }
}
public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult<AgentBranding>> ReadBrandingAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<AgentBranding>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () => { await using var db = await factory.CreateContextAsync(); return ServiceResult<AgentBranding>.Ok(await BrandingSettings.ReadAsync(db)); }, logger);
    }
    public async Task<ServiceResult<AgentBranding>> SaveBrandingAsync(AgentBranding branding, string expectedVersion)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<AgentBranding>.Fail("Pxe.Forbidden");
        if (branding.OrganizationName?.Length > 100 || branding.Tagline?.Length > 160 ||
            branding.PortalTitle?.Length > 100 || branding.HelpTabName?.Length > 40 || branding.SupportResponseMessage?.Length > 300 ||
            (branding.DarkBackgroundColor is not null && !AgentBranding.ValidColor(branding.DarkBackgroundColor)) ||
            (branding.DarkAccentColor is not null && !AgentBranding.ValidColor(branding.DarkAccentColor)) ||
            !AgentBranding.ValidColor(branding.BackgroundColor) || !AgentBranding.ValidColor(branding.AccentColor) || (branding.LogoPng is not null && !PortalIcon.IsValid(branding.LogoPng)))
            return ServiceResult<AgentBranding>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.BrandingSettings.UseUpTo100CharactersForOrganizationAndPortal));
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)");
            var previous = await BrandingSettings.ReadAsync(db);
            if (previous.Version != expectedVersion) return ServiceResult<AgentBranding>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.BrandingSettings.BrandingChangedSinceThisPageLoadedReloadBeforeSaving));
            var value = branding with { Version = Guid.NewGuid().ToString("N"), OrganizationName = branding.OrganizationName?.Trim() ?? "", Tagline = branding.Tagline?.Trim() ?? "",
                PortalTitle = branding.PortalTitleText, HelpTabName = branding.HelpTabText, SupportResponseMessage = branding.SupportResponseText };
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == BrandingSettings.Key);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = BrandingSettings.Key, Value = JsonSerializer.Serialize(value) });
            else row.Value = JsonSerializer.Serialize(value);
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Organization branding",
                ObjectJson = JsonSerializer.Serialize(new { value.OrganizationName, value.PortalTitle, value.HelpTabName, value.SupportResponseMessage, value.Tagline, value.BackgroundColor, value.AccentColor, value.DarkBackgroundColor, value.DarkAccentColor, value.Version, HasLogo = value.LogoPng is not null }) });
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return ServiceResult<AgentBranding>.Ok(value);
        }, logger);
    }
}
