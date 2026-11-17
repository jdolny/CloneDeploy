using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Handles cancellation and unresolved outcomes for action deployment tasks.
/// </summary>
public sealed partial class ActionDeploymentService
{
    public async Task<ServiceResult> ResolveUnknownAsync(Guid taskId)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            var user = access?.User;
            if (user is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await DeviceTaskQueue.LockAsync(db);
            var task = await db.DeviceTasks.Include(x => x.Deployment).SingleOrDefaultAsync(x => x.Id == taskId);
            if (task is not null && !access!.CanManage(task.Deployment.RequestedByUserId)) return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);
            if (task is null || task.Status != DeviceTaskStatus.Unknown)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Tasks.OnlyTasksWithAnUnknownOutcomeCanBeResolved));

            task.Status = DeviceTaskStatus.Failed;
            task.LeaseToken = null;
            task.LeaseExpiresAtUtc = null;
            task.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
            task.Result = "Operator confirmed execution is no longer running. Submit a new deployment to retry.";
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                AuditType = AuditType.Update,
                ObjectType = nameof(DeviceTask),
                ObjectName = taskId.ToString(),
                ObjectJson = "{\"Operation\":\"ResolveUnknown\"}"
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult.Ok();
        });
    }

    public async Task<ServiceResult> CancelAsync(Guid deploymentId)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            var user = access?.User;
            if (user is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await DeviceTaskQueue.LockAsync(db);
            var deployment = await db.ActionDeployments.SingleOrDefaultAsync(x => x.Id == deploymentId);
            if (deployment is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Tasks.DeploymentNotFound));

            if (!access!.CanManage(deployment.RequestedByUserId)) return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            var now = clock.GetUtcNow().UtcDateTime;
            var changed = await db.DeviceTasks.Where(x => x.DeploymentId == deploymentId && (x.Status == DeviceTaskStatus.Queued || x.Status == DeviceTaskStatus.Offered))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DeviceTaskStatus.Cancelled).SetProperty(x => x.FinishedAtUtc, now)
                    .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null));
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                AuditType = AuditType.Update,
                ObjectType = nameof(ActionDeployment),
                ObjectName = deploymentId.ToString(),
                ObjectJson = JsonSerializer.Serialize(new
                {
                    Operation = "CancelPending",
                    Count = changed
                })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult.Ok();
        });
    }

    public async Task<ServiceResult> CancelTaskAsync(Guid taskId)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            var user = access?.User;
            if (user is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await DeviceTaskQueue.LockAsync(db);
            var task = await db.DeviceTasks.Include(x => x.Deployment).SingleOrDefaultAsync(x => x.Id == taskId);
            if (task is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Tasks.DeviceTaskNotFound));

            if (!access!.CanManage(task.Deployment.RequestedByUserId)) return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            if (task.Status == DeviceTaskStatus.Cancelled)
                return ServiceResult.Ok();

            if (task.Status is not (DeviceTaskStatus.Queued or DeviceTaskStatus.Offered))
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Tasks.ThisTaskHasAlreadyStartedOrFinishedAndCannot));

            task.Status = DeviceTaskStatus.Cancelled;
            task.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
            task.LeaseToken = null;
            task.LeaseExpiresAtUtc = null;
            task.Result = "Cancelled by an operator before execution started.";
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                AuditType = AuditType.Update,
                ObjectType = nameof(DeviceTask),
                ObjectName = taskId.ToString(),
                ObjectJson = JsonSerializer.Serialize(new { Operation = "CancelTask", task.DeploymentId, task.DeviceName })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult.Ok();
        });
    }
}