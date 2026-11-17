using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AutomaticDeploymentMatchDto(int Id, string Name, int ImageId, string ImageName,
    int ProfileId, string ProfileName, string? UnavailableReason);
public sealed record AutomaticDeploymentMatchRequest(string? Manufacturer, string? Model);

public static class AutomaticDeploymentMatching
{
    public static List<AutomaticDeploymentRule> Resolve(IEnumerable<AutomaticDeploymentRule> rules, string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model)) return [];
        var matches = rules.Where(x => x.Enabled && x.IsComplete && DriverMatching.Matches(new(x.Id, x.Name,
            x.ManufacturerRegex, x.ModelRegex, x.Manufacturer is null || x.Model is null ? [] : [new(x.Manufacturer, x.Model, 0)]),
            manufacturer, model)).ToList();
        return matches.Count == 0 ? [] : matches.Where(x => x.Priority == matches.Max(r => r.Priority)).OrderBy(x => x.Name).ThenBy(x => x.Id).ToList();
    }
}

public sealed partial class ImagingDeploymentService
{
    public async Task<List<InventoriedModelDto>> GetAutomaticRuleModelsAsync()
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentRuleView) is null) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var rows = await db.DeviceComputerSystems.AsNoTracking().Where(x => x.Device!.ArchivedAtUtc == null &&
                                                                            x.Manufacturer != null && x.Model != null && x.Manufacturer.Trim() != "" && x.Model.Trim() != "")
            .Select(x => new { x.DeviceId, Manufacturer = x.Manufacturer!.Trim(), Model = x.Model!.Trim() }).ToListAsync();
        return rows.GroupBy(x => (x.Manufacturer.ToUpperInvariant(), x.Model.ToUpperInvariant()))
            .Select(g => new InventoriedModelDto(g.First().Manufacturer, g.First().Model, g.Select(x => x.DeviceId).Distinct().Count()))
            .OrderBy(x => x.Manufacturer).ThenBy(x => x.Model).ToList();
    }

    public async Task<List<AutomaticDeploymentRule>> GetAutomaticRulesAsync()
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentRuleView) is null) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        return await db.Set<AutomaticDeploymentRule>().AsNoTracking().Include(x => x.Image).Include(x => x.DeploymentProfile)
            .OrderByDescending(x => x.Priority).ThenBy(x => x.Name).ToListAsync();
    }

    public async Task SaveAutomaticRuleAsync(AutomaticDeploymentRule input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentRuleEdit) ?? throw new UnauthorizedAccessException();
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var name = Clean(input.Name);
        if (name is null || name.Length > 100) throw new InvalidOperationException("Enter a rule name of up to 100 characters.");
        var match = new DeploymentDriverRule { Manufacturer = Clean(input.Manufacturer), Model = Clean(input.Model),
            ManufacturerRegex = Clean(input.ManufacturerRegex), ModelRegex = Clean(input.ModelRegex) };
        if (DeploymentProfileService.GetDriverRuleValidationError(match) is { } error) throw new InvalidOperationException(error);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await db.Images.AnyAsync(x => x.Id == input.ImageId) || !await db.ImageProfiles.AnyAsync(x => x.Id == input.DeploymentProfileId))
            throw new InvalidOperationException("Select an image and deployment profile.");
        var rule = input.Id == 0 ? new AutomaticDeploymentRule() : await db.Set<AutomaticDeploymentRule>().SingleAsync(x => x.Id == input.Id);
        rule.Name = name; rule.Enabled = input.Enabled; rule.Priority = input.Priority;
        rule.Description = Clean(input.Description);
        rule.Manufacturer = match.Manufacturer; rule.Model = match.Model;
        rule.ManufacturerRegex = match.ManufacturerRegex; rule.ModelRegex = match.ModelRegex;
        rule.ImageId = input.ImageId; rule.DeploymentProfileId = input.DeploymentProfileId;
        if (input.Id == 0) db.Add(rule);
        await db.SaveChangesAsync();
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = input.Id == 0 ? AuditType.Create : AuditType.Update, ObjectType = nameof(AutomaticDeploymentRule), ObjectId = rule.Id,
            ObjectName = rule.Name, ObjectJson = JsonSerializer.Serialize(new { rule.Name, rule.Description, rule.Enabled, rule.Priority, rule.Manufacturer, rule.Model,
                rule.ManufacturerRegex, rule.ModelRegex, rule.ImageId, rule.DeploymentProfileId }) });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public Task SaveAutomaticRuleGeneralAsync(AutomaticDeploymentRule input) => SaveAutomaticRuleSectionAsync(input, "General");
    public Task SaveAutomaticRuleMatchingAsync(AutomaticDeploymentRule input) => SaveAutomaticRuleSectionAsync(input, "Matching");
    public Task SaveAutomaticRuleDeploymentAsync(AutomaticDeploymentRule input) => SaveAutomaticRuleSectionAsync(input, "Deployment");

    private async Task SaveAutomaticRuleSectionAsync(AutomaticDeploymentRule input, string section)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentRuleEdit) ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var creating = input.Id == 0;
        var rule = creating ? new AutomaticDeploymentRule() : await db.Set<AutomaticDeploymentRule>().SingleAsync(x => x.Id == input.Id);
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (section)
        {
            case "General":
                var name = Clean(input.Name);
                if (name is null || name.Length > 100) throw new InvalidOperationException("Enter a rule name of up to 100 characters.");
                rule.Name = name;
                rule.Description = Clean(input.Description);
                rule.Enabled = input.Enabled;
                rule.Priority = input.Priority;
                break;
            case "Matching":
                var match = new DeploymentDriverRule { Manufacturer = Clean(input.Manufacturer), Model = Clean(input.Model),
                    ManufacturerRegex = Clean(input.ManufacturerRegex), ModelRegex = Clean(input.ModelRegex) };
                if (DeploymentProfileService.GetDriverRuleValidationError(match) is { } error) throw new InvalidOperationException(error);
                rule.Manufacturer = match.Manufacturer;
                rule.Model = match.Model;
                rule.ManufacturerRegex = match.ManufacturerRegex;
                rule.ModelRegex = match.ModelRegex;
                break;
            case "Deployment":
                if (!await db.Images.AnyAsync(x => x.Id == input.ImageId) || !await db.ImageProfiles.AnyAsync(x => x.Id == input.DeploymentProfileId))
                    throw new InvalidOperationException("Select an image and deployment profile.");
                rule.ImageId = input.ImageId;
                rule.DeploymentProfileId = input.DeploymentProfileId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section));
        }
        if (creating) db.Add(rule);
        await db.SaveChangesAsync();
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = creating ? AuditType.Create : AuditType.Update, ObjectType = nameof(AutomaticDeploymentRule), ObjectId = rule.Id,
            ObjectName = rule.Name, ObjectJson = JsonSerializer.Serialize(new { Section = section, rule.Name, rule.Description, rule.Enabled, rule.Priority,
                rule.Manufacturer, rule.Model, rule.ManufacturerRegex, rule.ModelRegex, rule.ImageId, rule.DeploymentProfileId }) });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        input.Id = rule.Id;
    }

    public async Task DeleteAutomaticRuleAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DeploymentRuleEdit) ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var rule = await db.Set<AutomaticDeploymentRule>().SingleAsync(x => x.Id == id);
        db.Remove(rule);
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = AuditType.Delete, ObjectType = nameof(AutomaticDeploymentRule), ObjectId = id, ObjectName = rule.Name,
            ObjectJson = JsonSerializer.Serialize(new { rule.Name }) });
        await db.SaveChangesAsync();
    }

    public async Task<List<AutomaticDeploymentMatchDto>> MatchAutomaticRulesAsync(AutomaticDeploymentMatchRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var rules = await db.Set<AutomaticDeploymentRule>().AsNoTracking().Where(x => x.Enabled)
            .Include(x => x.Image).Include(x => x.DeploymentProfile).ToListAsync(ct);
        var matches = AutomaticDeploymentMatching.Resolve(rules, request.Manufacturer, request.Model);
        if (matches.Count == 0) return [];
        var catalog = await ManualCatalogAsync(ct);
        // Keep unavailable winners visible; never silently fall back to a different image.
        return matches.Select(x => new AutomaticDeploymentMatchDto(x.Id, x.Name, x.ImageId!.Value, x.Image!.Name,
            x.DeploymentProfileId!.Value, x.DeploymentProfile!.Name,
            catalog.Images.SingleOrDefault(i => i.Id == x.ImageId) is not { } image ? "Image is disabled or unavailable." :
            image.UnavailableReason ?? (catalog.Profiles.Any(p => p.Id == x.DeploymentProfileId) ? null : "Deployment profile is disabled or hidden from WinPE."))).ToList();
    }
}
