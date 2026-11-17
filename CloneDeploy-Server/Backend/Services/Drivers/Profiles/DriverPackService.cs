using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages driver profiles, their packages, and device matching rules.
/// </summary>
public sealed class DriverPackService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<DriverPack>>> GetAsync(string? search = null, bool includeDisabled = false)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<DriverPack>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var query = db.DriverProfiles.AsNoTracking().Include(x => x.Tags).Include(x => x.Models).Include(x => x.RegexRules).AsSplitQuery().AsQueryable();
        if (!includeDisabled) query = query.Where(x => x.Enabled);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text) || (x.Description != null && x.Description.ToLower().Contains(text)));
        }

        return ServiceResult<List<DriverPack>>.Ok(await query.OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<DriverPack>> GetByIdAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<DriverPack>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var entity = await db.DriverProfiles.AsNoTracking().Include(x => x.Tags).Include(x => x.Models).Include(x => x.RegexRules).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id);
        return entity is null ? ServiceResult<DriverPack>.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound)) : ServiceResult<DriverPack>.Ok(entity);
    }

    public async Task<ServiceResult<DriverPack>> CreateAsync(string name, string? description)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult<DriverPack>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        name = Clean(name) ?? string.Empty;
        if (name.Length is 0 or > 200)
            return ServiceResult<DriverPack>.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));
        if (await db.DriverProfiles.AnyAsync(x => x.Name == name))
            return ServiceResult<DriverPack>.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.ThisDriverProfileNameIsAlreadyInUse));
        var entity = new DriverPack
        {
            Name = name,
            Description = description?.Trim()
        };
        db.DriverProfiles.Add(entity);
        await db.SaveChangesAsync();
        Audit(db, user, entity, AuditType.Create, new { entity.Name, entity.Description });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<DriverPack>.Ok(entity);
    }

    public async Task<ServiceResult> SaveGeneralAsync(DriverPack input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.DriverProfiles.Include(x => x.Tags).SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
        var name = Clean(input.Name) ?? string.Empty;
        if (name.Length is 0 or > 200)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.EnterANameBetween1And200Characters));
        if (await db.DriverProfiles.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.ThisDriverProfileNameIsAlreadyInUse));
        var tags = input.Tags.Select(x => x.TagId).Distinct().ToList();
        if (await db.Tags.CountAsync(x => tags.Contains(x.Id)) != tags.Count)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.ASelectedTagNoLongerExists));
        entity.Name = name;
        entity.Description = input.Description?.Trim();
        db.RemoveRange(entity.Tags.Where(x => !tags.Contains(x.TagId)));
        var existing = entity.Tags.Select(x => x.TagId).ToHashSet();
        foreach (var id in tags.Except(existing)) entity.Tags.Add(new DriverProfileTag { TagId = id });
        Audit(db, user, entity, AuditType.Update, new
        {
            Section = "General", entity.Name, entity.Description,
            Tags = tags
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<InventoriedModelDto>>> GetInventoryAsync()
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<InventoriedModelDto>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<InventoriedModelDto>>.Ok(await InventoryAsync(db));
    }

    public async Task<ServiceResult<List<InventoriedModelDto>>> PreviewMatchingAsync(DriverPack input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<InventoriedModelDto>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var validationError = DriverMatching.GetPackValidationError(input);
        if (validationError is not null)
            return ServiceResult<List<InventoriedModelDto>>.Fail(ErrorKeys.Driver.Validation, validationError);
        return ServiceResult<List<InventoriedModelDto>>.Ok((await InventoryAsync(db)).Where(x => DriverMatching.MatchesPack(input, x.Manufacturer, x.Model)).ToList());
    }

    public async Task<ServiceResult> SaveMatchingAsync(DriverPack input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var validationError = DriverMatching.GetPackValidationError(input);
        if (validationError is not null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, validationError);
        var entity = await db.DriverProfiles.Include(x => x.Models).Include(x => x.RegexRules).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
        var inventory = await InventoryAsync(db);
        var requested = input.Models.Select(x => new
            {
                Manufacturer = x.Manufacturer.Trim(),
                Model = x.Model.Trim()
            })
            .DistinctBy(x => (x.Manufacturer.ToUpperInvariant(), x.Model.ToUpperInvariant())).ToList();
        foreach (var link in requested)
            if (!inventory.Any(x => x.Manufacturer.Equals(link.Manufacturer, StringComparison.OrdinalIgnoreCase) && x.Model.Equals(link.Model, StringComparison.OrdinalIgnoreCase)) &&
                !entity.Models.Any(x => x.Manufacturer.Equals(link.Manufacturer, StringComparison.OrdinalIgnoreCase) && x.Model.Equals(link.Model, StringComparison.OrdinalIgnoreCase)))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.ChooseManufacturerAndModelPairsFromInventory));
        // Exercise expressions against known models before publishing rules for imaging.
        foreach (var model in inventory) _ = DriverMatching.MatchesPack(input, model.Manufacturer, model.Model);
        db.DriverModelLinks.RemoveRange(entity.Models);
        db.RemoveRange(entity.RegexRules);
        await db.SaveChangesAsync();
        entity.Models = requested.Select(x => new DriverModelLink
        {
            Manufacturer = x.Manufacturer,
            Model = x.Model
        }).ToList();
        entity.ManufacturerRegex = Clean(input.ManufacturerRegex);
        entity.ModelRegex = Clean(input.ModelRegex);
        entity.RegexRules = input.RegexRules.Select(r => (Manufacturer: Clean(r.ManufacturerRegex), Model: Clean(r.ModelRegex)))
            .Distinct().Select(r => new DriverPackRegexRule { ManufacturerRegex = r.Manufacturer, ModelRegex = r.Model }).ToList();
        Audit(db, user, entity, AuditType.Update, new
        {
            Section = "Matching", entity.ManufacturerRegex, entity.ModelRegex,
            Models = requested, RegexRules = entity.RegexRules.Select(r => new { r.ManufacturerRegex, r.ModelRegex }).ToList()
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SaveConfigurationAsync(DriverPack input)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.DriverProfiles.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
        if (input.UpdateScheduleId is int id)
        {
            var schedule = await db.Schedules.FindAsync(id);
            if (schedule is null || !new ScheduleCalculator().IsValid(schedule))
                return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.SelectAValidSchedule));
        }

        if (entity.UpdateScheduleId != input.UpdateScheduleId) entity.LastEvaluatedAtUtc = DateTime.UtcNow;
        entity.UpdateScheduleId = input.UpdateScheduleId;
        entity.Enabled = input.Enabled;
        Audit(db, user, entity, AuditType.Update, new { Section = "Configuration", entity.Enabled, entity.UpdateScheduleId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<Schedule>>> GetSchedulesAsync()
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ScheduleView);
        if (user is null)
            return ServiceResult<List<Schedule>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<Schedule>>.Ok(await db.Schedules.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<DeploymentProfile>>> GetUsedByAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<DeploymentProfile>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<DeploymentProfile>>.Ok(await db.ImageProfiles.AsNoTracking()
            .Where(x => x.DriverPackId == id || db.DeploymentDriverRules.Any(rule => rule.DriverPackId == id && rule.DeploymentProfileId == x.Id)).OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await db.DriverProfiles.SingleOrDefaultAsync(x => x.Id == id);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverFileService.DriverProfileNotFound));
        if (await db.ImageProfiles.AnyAsync(x => x.DriverPackId == id) || await db.DeploymentDriverRules.AnyAsync(x => x.DriverPackId == id))
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.RemoveThisDriverProfileFromImageProfilesFirstOr));
        if (await db.DriverFiles.AnyAsync(x => x.DriverProfileId == id))
            return ServiceResult.Fail(ErrorKeys.Driver.Validation, UiText.Get(LocKeys.Ui.DriverPackService.RemoveTheDriverFilesBeforeDeletingThisProfile));
        Audit(db, user, entity, AuditType.Delete, new { entity.Name });
        db.DriverProfiles.Remove(entity);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<AuditLog>>> GetAuditAsync(int id)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.DriverPackView);
        if (user is null)
            return ServiceResult<List<AuditLog>>.Fail(ErrorKeys.Driver.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<AuditLog>>.Ok(await db.AuditLogs.AsNoTracking().Where(x => x.ObjectType == nameof(DriverPack) && x.ObjectId == id)
            .OrderByDescending(x => x.LoggedAtUtc).Take(200).ToListAsync());
    }

    private static DriverMatchRuleDto Rule(DriverPack p) => new(p.Id, p.Name, p.ManufacturerRegex, p.ModelRegex,
        p.Models.Select(x => new InventoriedModelDto(x.Manufacturer, x.Model, 0)).ToList());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<List<InventoriedModelDto>> InventoryAsync(CloneDeployDbContext db)
    {
        var rows = await db.DeviceComputerSystems.AsNoTracking().Where(x => x.Device!.ArchivedAtUtc == null &&
                                                                            x.Manufacturer != null && x.Model != null && x.Manufacturer.Trim() != "" && x.Model.Trim() != "")
            .Select(x => new
            {
                x.DeviceId,
                Manufacturer = x.Manufacturer!.Trim(),
                Model = x.Model!.Trim()
            }).ToListAsync();
        return rows.GroupBy(x => (x.Manufacturer.ToUpperInvariant(), x.Model.ToUpperInvariant()))
            .Select(g => new InventoriedModelDto(g.First().Manufacturer, g.First().Model, g.Select(x => x.DeviceId).Distinct().Count()))
            .OrderBy(x => x.Manufacturer).ThenBy(x => x.Model).ToList();
    }

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, DriverPack entity, AuditType type, object detail) =>
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity!.Name!,
            AuditType = type,
            ObjectType = nameof(DriverPack),
            ObjectId = entity.Id,
            ObjectName = entity.Name,
            ObjectJson = JsonSerializer.Serialize(detail)
        });

}
