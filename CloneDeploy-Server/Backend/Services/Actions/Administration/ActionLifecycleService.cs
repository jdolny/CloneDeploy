using System.Data;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Archives, restores, and deletes actions and cleans up their related files and schedules.
/// </summary>
public sealed class ActionLifecycleService(
    ICloneDeployDbFactory factory,
    StoredFileStorage storage,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    TimeProvider clock,
    ILogger<ActionLifecycleService> logger)
{
    public async Task<ServiceResult<List<ActionRelationDto>>> GetRelationsAsync(Guid actionGuid)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView) is null) return ServiceResult<List<ActionRelationDto>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var reference = await db.ActionReferences.AsNoTracking().Include(x => x.ExternalFileUpdateSchedule)
            .SingleOrDefaultAsync(x => x.ActionGuid == actionGuid);
        if (reference is null) return ServiceResult<List<ActionRelationDto>>.Fail(ErrorKeys.Common.NotFound, actionGuid);
        return ServiceResult<List<ActionRelationDto>>.Ok(await ActionRelations.ReadAsync(db, reference));
    }

    public async Task<ServiceResult<List<ActionExecutionHistoryDto>>> GetExecutionHistoryAsync(Guid actionGuid)
    {
        if (await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyView) is null) return ServiceResult<List<ActionExecutionHistoryDto>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (!await db.ActionReferences.AnyAsync(x => x.ActionGuid == actionGuid))
            return ServiceResult<List<ActionExecutionHistoryDto>>.Fail(ErrorKeys.Common.NotFound, actionGuid);
        return ServiceResult<List<ActionExecutionHistoryDto>>.Ok(await db.DeviceTasks.AsNoTracking()
            .Where(x => x.Deployment.ActionGuid == actionGuid)
            .OrderByDescending(x => x.Deployment.CreatedAtUtc).ThenByDescending(x => x.Id).Take(200)
            .Select(x => new ActionExecutionHistoryDto(x.DeploymentId, x.DeviceName, x.Deployment.RequestedBy,
                x.Deployment.CreatedAtUtc, x.FinishedAtUtc, x.Status, x.Result)).ToListAsync());
    }

    public Task<ServiceResult> ArchiveAsync(Guid actionGuid) => SetArchivedAsync(actionGuid, true);

    public Task<ServiceResult> ArchiveManyAsync(IEnumerable<Guid> actionGuids) => SetArchivedAsync(actionGuids, true);

    public Task<ServiceResult> RestoreAsync(Guid actionGuid) => SetArchivedAsync(actionGuid, false);

    public Task<ServiceResult> DeleteAsync(Guid actionGuid) => DeleteManyAsync([actionGuid]);

    // All-or-nothing: a missing or referenced action leaves the entire selection unchanged.
    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<Guid> actionGuids)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        var guids = actionGuids.Distinct().ToArray();
        if (guids.Length == 0)
            return ServiceResult.Ok();

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await DeviceTaskQueue.LockAsync(db);
            var actions = await db.ActionReferences.Where(x => guids.Contains(x.ActionGuid)).ToListAsync();
            if (actions.Count != guids.Length)
                return ServiceResult.Fail(ErrorKeys.Common.NotFound);
            if (await IsReferencedAsync(db, guids))
                return ServiceResult.Fail(ErrorKeys.Action.InUse);

            var fileIds = await db.ActionFiles.Where(x => guids.Contains(x.ActionGuid))
                .Select(x => x.StoredFileId).Distinct().ToListAsync();
            foreach (var action in actions) AddAudit(db, user, action, AuditType.Delete);
            // The shared reference owns the action row, file links, and tags through cascade deletes.
            db.ActionReferences.RemoveRange(actions);
            await db.SaveChangesAsync();
            var unusedFiles = await db.StoredFiles.Where(x => fileIds.Contains(x.Id) &&
                                                              !db.ActionFiles.Any(link => link.StoredFileId == x.Id)).ToListAsync();
            db.StoredFiles.RemoveRange(unusedFiles);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            foreach (var file in unusedFiles)
            {
                try
                {
                    File.Delete(storage.NamedPath(file));
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Could not remove unused action file {FileId}", file.Id);
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogWarning(ex, "Could not remove unused action file {FileId}", file.Id);
                }
            }

            return ServiceResult.Ok();
        }
        finally
        {
            storage.Gate.Release();
        }
    }

    private Task<ServiceResult> SetArchivedAsync(Guid actionGuid, bool archived) => SetArchivedAsync([actionGuid], archived);

    // Validate the whole selection before changing archive state or writing audit records.
    private async Task<ServiceResult> SetArchivedAsync(IEnumerable<Guid> actionGuids, bool archived)
    {
        var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.PolicyEdit);
        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Action.Forbidden);

        var guids = actionGuids.Distinct().ToArray();
        if (guids.Length == 0)
            return ServiceResult.Ok();

        await storage.Gate.WaitAsync();
        try
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await DeviceTaskQueue.LockAsync(db);
            var actions = await db.ActionReferences.Where(x => guids.Contains(x.ActionGuid)).ToListAsync();
            if (actions.Count != guids.Length)
                return ServiceResult.Fail(ErrorKeys.Common.NotFound, guids.Except(actions.Select(x => x.ActionGuid)).First());
            var changes = actions.Where(x => x.ArchivedAtUtc.HasValue != archived).ToList();
            if (changes.Count == 0)
                return ServiceResult.Ok();
            if (archived && await IsReferencedAsync(db, changes.Select(x => x.ActionGuid).ToArray()))
                return ServiceResult.Fail(ErrorKeys.Action.InUse);

            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var action in changes)
            {
                action.ArchivedAtUtc = archived ? now : null;
                // Resume schedules from restoration, without catching up the archived interval.
                if (!archived) action.ExternalFilesLastEvaluatedAtUtc = now;
                AddAudit(db, user, action, archived ? AuditType.Archive : AuditType.Restore);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult.Ok();
        }
        finally
        {
            storage.Gate.Release();
        }
    }

    private static async Task<bool> IsReferencedAsync(CloneDeployDbContext db, Guid[] guids)
    {
        if (await db.DeviceTasks.AnyAsync(x => guids.Contains(x.Deployment.ActionGuid) &&
                                               (x.Status == DeviceTaskStatus.Queued || x.Status == DeviceTaskStatus.Offered || x.Status == DeviceTaskStatus.Running || x.Status == DeviceTaskStatus.Unknown))) return true;
        if (await db.PolicyActions.AnyAsync(x => guids.Contains(x.ActionGuid) ||
                                                 (x.ConditionNextAction.HasValue && guids.Contains(x.ConditionNextAction.Value)))) return true;
        var scriptIds = db.ScriptActions.Where(x => guids.Contains(x.Guid)).Select(x => x.Id);
        return await db.PolicyActions.AnyAsync(x => x.ConditionId.HasValue && scriptIds.Contains(x.ConditionId.Value)) ||
               await db.Policies.AnyAsync(x => x.ConditionId.HasValue && scriptIds.Contains(x.ConditionId.Value));
    }

    private void AddAudit(CloneDeployDbContext db, ClaimsPrincipal user, ActionReference action, AuditType type)
    {
        var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(),
            AuditType = type, ObjectType = nameof(ActionReference), ObjectId = 0,
            ObjectName = action.ActionGuid.ToString(), LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
            ObjectJson = JsonSerializer.Serialize(new { action.ActionGuid, action.ActionType, action.ArchivedAtUtc })
        });
    }
}
