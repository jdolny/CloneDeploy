using System.Data;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages saved instructions for setting up domain membership on devices.
/// </summary>
public sealed class DomainJoinActionService(
    ICloneDeployDbFactory dbFactory,
    ActionLifecycleService actionService,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<DomainJoinAction>>> GetAsync(string? searchText = null, bool includeArchived = false)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<DomainJoinAction>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var query = db.DomainJoinActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags).AsNoTracking();
        if (!includeArchived)
            query = query.Where(x => x.ActionReference!.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<DomainJoinAction>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<DomainJoinAction>> GetByIdAsync(int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<DomainJoinAction>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.DomainJoinActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == id);
        return entity is null ? ServiceResult<DomainJoinAction>.Fail(ErrorKeys.Common.NotFound, id) : ServiceResult<DomainJoinAction>.Ok(entity);
    }

    // Creation saves a named draft; configuration is validated when its own form is saved.
    public async Task<ServiceResult<DomainJoinAction>> CreateAsync(string name, string? description)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult<DomainJoinAction>.Fail(ErrorKeys.Action.Forbidden);

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<DomainJoinAction>.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        name = name.Trim();
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.DomainJoinActions.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
            return ServiceResult<DomainJoinAction>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new DomainJoinAction { Name = name, Description = description };
        db.DomainJoinActions.Add(entity);
        await db.SaveChangesAsync();
        AddAudit(db, user, entity, AuditType.Create, "General");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<DomainJoinAction>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateGeneralAsync(DomainJoinAction action)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        if (string.IsNullOrWhiteSpace(action.Name))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var editable = await ActionEditGuard.CheckAsync(db, action.Guid);
        if (!editable.Success) return editable;
        var entity = await db.DomainJoinActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        var name = action.Name.Trim();
        if (await db.DomainJoinActions.AnyAsync(x => x.Id != action.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, name);

        var tagIds = action.ActionReference?.Tags.Select(x => x.TagId).ToHashSet() ?? [];
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Count)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.CommandActionService.OneOrMoreTagsNoLongerExistReloadThe));

        entity.Name = name;
        entity.Description = action.Description;
        entity.ActionReference ??= new ActionReference { ActionGuid = entity.Guid, ActionType = ActionType.DomainJoin };
        db.ActionTags.RemoveRange(entity.ActionReference.Tags.Where(x => !tagIds.Contains(x.TagId)));
        var existing = entity.ActionReference.Tags.Select(x => x.TagId).ToHashSet();
        foreach (var id in tagIds.Except(existing))
            entity.ActionReference.Tags.Add(new ActionTag { ActionGuid = entity.Guid, TagId = id });

        AddAudit(db, user, entity, AuditType.Update, "General");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        action.Name = name;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> UpdateAsync(DomainJoinAction action)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        var error = ValidateConfiguration(action);
        if (error is not null)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, error);

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var editable = await ActionEditGuard.CheckAsync(db, action.Guid);
        if (!editable.Success) return editable;
        var entity = await db.DomainJoinActions.SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        if (!await db.ManagedCredentials.AnyAsync(x => x.Id == action.ManagedCredentialId))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.DomainJoinActionService.ChooseAnExistingDomainJoinCredential));
        var ruleError = await ValidateRuleSetAsync(db, action);
        if (ruleError is not null) return ServiceResult.Fail(ErrorKeys.Action.Validation, ruleError);
        entity.DomainName = action.DomainName.Trim();
        entity.OuMode = action.OuMode;
        entity.OuNamingRuleSetId = action.OuMode == DomainJoinOuMode.NamingRules ? action.OuNamingRuleSetId : null;
        entity.OrganizationalUnit = action.OuMode == DomainJoinOuMode.SpecificOu ? action.OrganizationalUnit!.Trim() : null;
        entity.ManagedCredentialId = action.ManagedCredentialId;
        AddAudit(db, user, entity, AuditType.Update, "Configuration");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<PolicyTargetDirectoryNodeDto>>> GetDirectoryOusAsync()
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView) is null)
            return ServiceResult<List<PolicyTargetDirectoryNodeDto>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await dbFactory.CreateContextAsync();
        var nodes = await (from node in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on node.DirectorySourceId equals source.Id
            where source.Provider == DirectoryProvider.ActiveDirectory && (source.IsEnabled && !source.ApplicationAccessOnly) && !node.IsMissing &&
                  (node.Kind == DirectoryObjectKind.Domain || node.Kind == DirectoryObjectKind.Container || node.Kind == DirectoryObjectKind.OrganizationalUnit)
            orderby source.Name, node.Name
            select new PolicyTargetDirectoryNodeDto(source.Id, source.Name, node.ObjectGuid, node.ParentGuid,
                node.Name, node.DistinguishedName, node.Kind == DirectoryObjectKind.OrganizationalUnit, true)).ToListAsync();
        return ServiceResult<List<PolicyTargetDirectoryNodeDto>>.Ok(nodes);
    }

    public async Task<ServiceResult<List<DomainJoinRuleChoice>>> GetRuleSetsAsync()
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView) is null)
            return ServiceResult<List<DomainJoinRuleChoice>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DomainJoinRuleChoice>>.Ok(await db.OuNamingRuleSets.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new DomainJoinRuleChoice(x.Id, x.Name, x.DomainName, x.Enabled)).ToListAsync());
    }

    internal static async Task<string?> ValidateRuleSetAsync(CloneDeployDbContext db, DomainJoinAction action)
    {
        if (action.OuMode != DomainJoinOuMode.NamingRules) return null;
        var set = await db.OuNamingRuleSets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == action.OuNamingRuleSetId);
        if (set is null || !set.Enabled) return "Choose an enabled OU naming rule set.";
        if (!set.DomainName.Equals(action.DomainName.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return "The OU naming rule set must belong to the join domain.";
        return OuNamingResolver.Validate(set, await OuNamingResolver.CatalogAsync(db));
    }

    public async Task<ServiceResult<List<Tag>>> GetTagsAsync()
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.TagView);
        if (user is null)
            return ServiceResult<List<Tag>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<Tag>>.Ok(await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult> ArchiveAsync(Guid guid)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit) is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        return await actionService.ArchiveAsync(guid);
    }

    public async Task<ServiceResult> ArchiveManyAsync(IEnumerable<Guid> guids)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit) is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        return await actionService.ArchiveManyAsync(guids);
    }

    public async Task<ServiceResult> RestoreAsync(Guid guid)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit) is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        return await actionService.RestoreAsync(guid);
    }

    public async Task<ServiceResult> DeleteAsync(Guid guid)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit) is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        return await actionService.DeleteAsync(guid);
    }

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<Guid> guids)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit) is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        return await actionService.DeleteManyAsync(guids);
    }


    public async Task<ServiceResult<List<Policy>>> GetUsedByAsync(Guid guid)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<Policy>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var policyIds = db.PolicyActions.Where(x => x.ActionGuid == guid || x.ConditionNextAction == guid).Select(x => x.PolicyId);
        return ServiceResult<List<Policy>>.Ok(await db.Policies.AsNoTracking().Where(x => policyIds.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<AuditLog>>> GetAuditAsync(Guid guid)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<AuditLog>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<AuditLog>>.Ok(await db.AuditLogs.AsNoTracking()
            .Where(x => x.ObjectType == nameof(ActionReference) && x.ObjectName == guid.ToString())
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync());
    }

    private static void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, DomainJoinAction action, AuditType type, string section)
    {
        var id = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = id, UserName = user.Identity?.Name ?? id.ToString(), AuditType = type,
            ObjectType = nameof(ActionReference), ObjectId = action.Id, ObjectName = action.Guid.ToString(),
            ObjectJson = JsonSerializer.Serialize(new { ActionGuid = action.Guid, ActionType = ActionType.DomainJoin, Section = section })
        });
    }

    internal static string? ValidateConfiguration(DomainJoinAction action)
    {
        var domain = action.DomainName?.Trim();
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Any(char.IsControl))
            return "Enter a valid domain DNS name, such as school.example.org.";
        var ou = action.OrganizationalUnit?.Trim();
        if (!System.Enum.IsDefined(action.OuMode)) return "Choose a valid OU selection mode.";
        if (action.OuMode == DomainJoinOuMode.NamingRules && action.OuNamingRuleSetId is not > 0) return "Choose an OU naming rule set.";
        if (action.OuMode == DomainJoinOuMode.SpecificOu && (string.IsNullOrEmpty(ou) || ou.Length > 2000 || ou.Any(char.IsControl) ||
            !ou.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || !ou.Contains(",DC=", StringComparison.OrdinalIgnoreCase)))
            return "Enter a full OU distinguished name, such as OU=Computers,DC=school,DC=example,DC=org. Choose Domain default mode to omit the OU.";
        if (action.ManagedCredentialId is not > 0) return "Choose a managed domain-join credential.";
        return null;
    }
}

public sealed record DomainJoinRuleChoice(int Id, string Name, string DomainName, bool Enabled);
