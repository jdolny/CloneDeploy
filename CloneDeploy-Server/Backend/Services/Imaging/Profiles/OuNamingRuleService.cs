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

public sealed class OuNamingRuleService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    private Task<ClaimsPrincipal?> User(string permission) => ImagingPermissions.BrowserAsync(authentication, authorization, permission);
    public async Task<ServiceResult<List<OuNamingRuleSet>>> ListAsync()
    {
        if (await User(Constants.Permissions.OuNamingRuleView) is null) return ServiceResult<List<OuNamingRuleSet>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<OuNamingRuleSet>>.Ok(await db.OuNamingRuleSets.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }
    public async Task<ServiceResult<OuNamingCatalog>> CatalogAsync()
    {
        if (await User(Constants.Permissions.OuNamingRuleView) is null) return ServiceResult<OuNamingCatalog>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<OuNamingCatalog>.Ok(await OuNamingResolver.CatalogAsync(db));
    }
    public async Task<ServiceResult<OuNamingResult>> PreviewAsync(OuNamingRuleSet draft, string computerName)
    {
        if (await User(Constants.Permissions.OuNamingRuleView) is null) return ServiceResult<OuNamingResult>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<OuNamingResult>.Ok(OuNamingResolver.Evaluate(draft, computerName, await OuNamingResolver.CatalogAsync(db)));
    }
    public async Task<ServiceResult> SaveAsync(OuNamingRuleSet input)
    {
        var user = await User(Constants.Permissions.OuNamingRuleEdit);
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var draft = !input.Enabled && input.DirectorySourceId is null && input.DomainName == "" &&
            input.RulesJson == "[]" && input.Fallback == OuNoMatch.Fail && input.FallbackOuGuid is null;
        var error = string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 || input.Name.Any(char.IsControl) || input.Description.Length > 2000
            ? "Enter a name of up to 200 printable characters and a description of up to 2000 characters."
            : draft ? null : OuNamingResolver.Validate(input, await OuNamingResolver.CatalogAsync(db));
        if (error is not null) return ServiceResult.Fail("Image.Validation", error);
        var name = input.Name.Trim();
        if (await db.OuNamingRuleSets.AnyAsync(x => x.Id != input.Id && x.Name == name)) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ARuleSetWithThisNameAlreadyExists));
        var set = input.Id == 0 ? new OuNamingRuleSet() : await db.OuNamingRuleSets.FindAsync(input.Id);
        if (set is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.RuleSetNoLongerExists));
        if (set.Revision != input.Revision) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetChangedReopenItBeforeSaving));
        var type = input.Id == 0 ? AuditType.Create : AuditType.Update;
        if (input.Id == 0) db.OuNamingRuleSets.Add(set);
        set.Description = input.Description.Trim();
        set.Name = name; set.DirectorySourceId = input.DirectorySourceId; set.DomainName = input.DomainName.ToLowerInvariant();
        set.Enabled = input.Enabled; set.Fallback = input.Fallback;
        set.FallbackOuGuid = input.Fallback == OuNoMatch.SpecificOu ? input.FallbackOuGuid : null;
        set.Rules = input.Rules; set.Revision++;
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.SaveChangesAsync();
            db.AuditLogs.Add(Audit(user, set, type));
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException) { return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetChangedReopenItBeforeSaving)); }
        catch (DbUpdateException) { return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.TheRuleSetCouldNotBeSavedRefreshThe)); }
        input.Id = set.Id; input.Revision = set.Revision;
        return ServiceResult.Ok();
    }
    public async Task<ServiceResult> DeleteAsync(int id, int revision)
    {
        var user = await User(Constants.Permissions.OuNamingRuleEdit);
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var set = await db.OuNamingRuleSets.FindAsync(id);
        if (set is null || set.Revision != revision) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetChangedOrWasDeletedRefreshBefore));
        if (await db.DomainJoinActions.AnyAsync(x => x.OuNamingRuleSetId == id)) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetIsUsedByADomainJoin));
        if (await db.ImageProfiles.AnyAsync(x => x.OuNamingRuleSetId == id)) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetIsUsedByADeploymentProfile));
        db.OuNamingRuleSets.Remove(set); db.AuditLogs.Add(Audit(user, set, AuditType.Delete));
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetChangedRefreshBeforeDeleting)); }
        catch (DbUpdateException) { return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.OuNamingRuleService.ThisRuleSetIsStillReferencedAndCannotBe)); }
        return ServiceResult.Ok();
    }
    private static AuditLog Audit(ClaimsPrincipal user, OuNamingRuleSet set, AuditType type) => new()
    {
        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
        AuditType = type, ObjectType = nameof(OuNamingRuleSet), ObjectId = set.Id, ObjectName = set.Name,
        ObjectJson = JsonSerializer.Serialize(set)
    };
}
