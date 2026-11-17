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
/// Manages notifications that can be displayed on devices.
/// </summary>
public sealed class NotificationActionService(
    ICloneDeployDbFactory dbFactory,
    ActionLifecycleService actionService,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<NotificationAction>>> GetAsync(string? searchText = null, bool includeArchived = false)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<List<NotificationAction>>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var query = db.NotificationActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags).AsNoTracking();
        if (!includeArchived)
            query = query.Where(x => x.ActionReference!.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<NotificationAction>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<NotificationAction>> GetByIdAsync(int id)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView);
        if (user is null)
            return ServiceResult<NotificationAction>.Fail(ErrorKeys.Action.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.NotificationActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == id);
        return entity is null ? ServiceResult<NotificationAction>.Fail(ErrorKeys.Common.NotFound, id) : ServiceResult<NotificationAction>.Ok(entity);
    }

    // Creation saves a named draft; configuration is validated when its own form is saved.
    public async Task<ServiceResult<NotificationAction>> CreateAsync(string name, string? description)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult<NotificationAction>.Fail(ErrorKeys.Action.Forbidden);

        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<NotificationAction>.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.CustomAttribute.Validation));

        name = name.Trim();
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.NotificationActions.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
            return ServiceResult<NotificationAction>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new NotificationAction { Name = name, Description = description, Title = "", Body = "" };
        db.NotificationActions.Add(entity);
        await db.SaveChangesAsync();
        AddAudit(db, user, entity, AuditType.Create, "General");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<NotificationAction>.Ok(entity);
    }

    public async Task<ServiceResult> UpdateGeneralAsync(NotificationAction action)
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
        var entity = await db.NotificationActions.Include(x => x.ActionReference!).ThenInclude(x => x.Tags)
            .SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        var name = action.Name.Trim();
        if (await db.NotificationActions.AnyAsync(x => x.Id != action.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, name);

        var tagIds = action.ActionReference?.Tags.Select(x => x.TagId).ToHashSet() ?? [];
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Count)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.CommandActionService.OneOrMoreTagsNoLongerExistReloadThe));

        entity.Name = name;
        entity.Description = action.Description;
        entity.ActionReference ??= new ActionReference { ActionGuid = entity.Guid, ActionType = ActionType.Notification };
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

    public async Task<ServiceResult> UpdateAsync(NotificationAction action)
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
        var entity = await db.NotificationActions.SingleOrDefaultAsync(x => x.Id == action.Id && x.Guid == action.Guid);
        if (entity is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, action.Id);

        entity.Title = action.Title;
        entity.Body = action.Body;
        entity.Timeout = action.Timeout;
        entity.NoticeId = action.NoticeId;
        entity.ExpiresAtUtc = action.ExpiresAtUtc;
        entity.RequireAcknowledgment = action.RequireAcknowledgment;
        entity.RemindAtLogin = action.RequireAcknowledgment; // Compatibility with earlier agents.
        entity.Position = action.Position;
        entity.Branding = CloneDeploy_Agent.Contracts.NotificationBranding.Organization;
        entity.Kind = action.Kind;
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

    private static void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, NotificationAction action, AuditType type, string section)
    {
        var id = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = id, UserName = user.Identity?.Name ?? id.ToString(), AuditType = type,
            ObjectType = nameof(ActionReference), ObjectId = action.Id, ObjectName = action.Guid.ToString(),
            ObjectJson = JsonSerializer.Serialize(new { ActionGuid = action.Guid, ActionType = ActionType.Notification, Section = section })
        });
    }

    private static string? ValidateConfiguration(NotificationAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            return "Enter a title.";
        if (string.IsNullOrWhiteSpace(action.Body))
            return "Enter notification text.";
        if (action.Title.Length > 200 || action.Body.Length > 2000)
            return "Use up to 200 characters for the title and 2,000 for the notification.";
        if (!System.Enum.IsDefined(action.Kind)) return "Select a valid notification type.";
        if (!System.Enum.IsDefined(action.Position)) return "Select a valid notification position.";
        if (action.NoticeId == Guid.Empty) return "Issue a valid notice.";
        if (action.RequireAcknowledgment && (action.ExpiresAtUtc.Kind != DateTimeKind.Utc || action.ExpiresAtUtc <= DateTime.UtcNow))
            return "Choose a future expiration date for the required notice.";
        if (action.Timeout is < 0 or > 86400)
            return "Popup timeout must be between 0 and 86,400 seconds.";
        return null;
    }
}
