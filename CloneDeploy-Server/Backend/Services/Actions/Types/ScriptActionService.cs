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
/// Manages scripts and the settings used to run them on devices.
/// </summary>
public sealed class ScriptActionService(
    ICloneDeployDbFactory dbFactory,
    ActionLifecycleService actionService,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<ScriptAction>>> GetAsync(string? searchText = null, bool includeArchived = false)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<ScriptAction>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var query = db.ScriptActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags).AsNoTracking();
        if (!includeArchived)
            query = query.Where(x => x.ActionReference!.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<ScriptAction>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<ScriptAction>> GetByIdAsync(int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<ScriptAction>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.ScriptActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == id);
        return entity is null ? ServiceResult<ScriptAction>.Fail(ErrorKeys.Common.NotFound, id) : ServiceResult<ScriptAction>.Ok(entity);
    }

    // Creation saves a named draft; configuration is validated when its own form is saved.
    public async Task<ServiceResult<ScriptAction>> CreateAsync(string name, string? description)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult<ScriptAction>.Fail(ErrorKeys.Action.Forbidden);

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<ScriptAction>.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        name = name.Trim();
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.ScriptActions.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
            return ServiceResult<ScriptAction>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new ScriptAction { Name = name, Description = description, SuccessCodes = "0", RedirectStdOut = true, RedirectStdError = true };
        db.ScriptActions.Add(entity);
        await db.SaveChangesAsync();
        AddAudit(db, user, entity, AuditType.Create, "General");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<ScriptAction>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateGeneralAsync(ScriptAction action)
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
        var entity = await db.ScriptActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        var name = action.Name.Trim();
        if (await db.ScriptActions.AnyAsync(x => x.Id != action.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, name);

        var tagIds = action.ActionReference?.Tags.Select(x => x.TagId).ToHashSet() ?? [];
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Count)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.CommandActionService.OneOrMoreTagsNoLongerExistReloadThe));

        entity.Name = name;
        entity.Description = action.Description;
        entity.ActionReference ??= new ActionReference { ActionGuid = entity.Guid, ActionType = ActionType.Script };
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

    public async Task<ServiceResult> UpdateAsync(ScriptAction action)
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
        var entity = await db.ScriptActions.SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        if (action.ManagedCredentialId.HasValue && !await db.ManagedCredentials.AnyAsync(x => x.Id == action.ManagedCredentialId))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.CommandActionService.ChooseAnExistingManagedCredential));

        entity.ScriptType = action.ScriptType;
        entity.Arguments = action.Arguments;
        entity.ScriptContents = action.ScriptContents;
        entity.Timeout = action.Timeout;
        entity.WorkingDirectory = action.WorkingDirectory;
        entity.RedirectStdOut = action.RedirectStdOut;
        entity.RedirectStdError = action.RedirectStdError;
        entity.SuccessCodes = action.SuccessCodes;
        entity.AddInventoryCollection = action.AddInventoryCollection;
        entity.IsCondition = action.IsCondition;
        entity.ManagedCredentialId = action.ManagedCredentialId;
        AddAudit(db, user, entity, AuditType.Update, "Configuration");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<Tag>>> GetTagsAsync()
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.TagView);
        if (user is null)
            return ServiceResult<List<Tag>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<Tag>>.Ok(await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<List<ManagedCredential>>> GetCredentialsAsync()
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<ManagedCredential>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<ManagedCredential>>.Ok(await db.ManagedCredentials.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ManagedCredential { Id = x.Id, Name = x.Name, Username = x.Username }).ToListAsync());
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
        var scriptIds = db.ScriptActions.Where(x => x.Guid == guid).Select(x => x.Id);
        var policyIds = db.PolicyActions.Where(x => x.ActionGuid == guid || x.ConditionNextAction == guid ||
                                                    (x.ConditionId.HasValue && scriptIds.Contains(x.ConditionId.Value))).Select(x => x.PolicyId);
        return ServiceResult<List<Policy>>.Ok(await db.Policies.AsNoTracking().Where(x => policyIds.Contains(x.Id) ||
                                                                                          (x.ConditionId.HasValue && scriptIds.Contains(x.ConditionId.Value))).OrderBy(x => x.Name).ToListAsync());
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

    private static void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, ScriptAction action, AuditType type, string section)
    {
        var id = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = id, UserName = user.Identity?.Name ?? id.ToString(), AuditType = type,
            ObjectType = nameof(ActionReference), ObjectId = action.Id, ObjectName = action.Guid.ToString(),
            ObjectJson = JsonSerializer.Serialize(new { ActionGuid = action.Guid, ActionType = ActionType.Script, Section = section })
        });
    }

    private static string? ValidateConfiguration(ScriptAction action)
    {
        if (action.IsCondition && action.AddInventoryCollection)
            return "Choose one script purpose: Deployment, Condition, or Custom inventory.";
        if (string.IsNullOrWhiteSpace(action.ScriptContents))
            return "Enter script contents.";
        if (action.Timeout < 0)
            return "Timeout cannot be negative.";
        if (string.IsNullOrWhiteSpace(action.SuccessCodes) || !action.SuccessCodes.Split(',').All(x => int.TryParse(x.Trim(), out _)))
            return "Enter comma-separated integer success exit codes.";
        if (ScriptRuntime.ValidationError(action.ScriptType) is { } scriptError)
            return scriptError;
        if (action.ScriptType is ActionScriptType.Bash or ActionScriptType.Zsh &&
            action.ManagedCredentialId is not null)
            return "macOS scripts run as root and cannot use a Run As credential.";
        return null;
    }
}
