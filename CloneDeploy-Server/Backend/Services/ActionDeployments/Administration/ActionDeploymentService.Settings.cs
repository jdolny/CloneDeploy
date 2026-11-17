using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages limits on device tasks and how often devices check in.
/// </summary>
public sealed partial class ActionDeploymentService
{
    public async Task<ServiceResult<DeviceTaskLimitsDto>> GetLimitsAsync()
    {
        return await ExecuteAsync(async () =>
        {
            var user = await GetAdminAsync();
            if (user is null)
                return ServiceResult<DeviceTaskLimitsDto>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            return ServiceResult<DeviceTaskLimitsDto>.Ok(await DeviceTaskQueue.ReadLimitsAsync(db));
        });
    }

    public async Task<ServiceResult> SaveLimitsAsync(DeviceTaskLimitsDto limits)
    {
        return await ExecuteAsync(async () =>
        {
            var user = await GetAdminAsync();
            if (user is null)
                return ServiceResult.Fail(ErrorKeys.Deployment.Forbidden);

            if (limits.CheckinIntervalMinutes is < 1 or > 1440)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Settings.CheckInIntervalMustBeBetween1And1));

            if (limits.EnrollmentRequestsPerMinute is < 1 or > AgentEnrollmentRateSettings.MaximumLimit)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Settings.EnrollmentRequestsPerMinuteMustBeBetween1And));

            if (limits.MaxConcurrentInventorySubmissions is < 1 or > 10000 || limits.MaxConcurrentDownloads is < 1 or > 10000 || limits.MaxConcurrentTasks is < 1 or > 10000 || limits.MaxConcurrentCheckins is < 1 or > 10000)
                return ServiceResult.Fail(ErrorKeys.Deployment.Validation, UiText.Get(LocKeys.Ui.ActionDeploymentService_Settings.ConcurrencyLimitsMustBeBetween1And10000));

            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await DeviceTaskQueue.LockAsync(db);
            var settings = new Dictionary<string, int>
            {
                [AgentEnrollmentRateSettings.Key] = limits.EnrollmentRequestsPerMinute,
                [DeviceTaskQueue.InventoryLimitKey] = limits.MaxConcurrentInventorySubmissions,
                [DeviceTaskQueue.DownloadLimitKey] = limits.MaxConcurrentDownloads,
                [DeviceTaskQueue.CheckinIntervalKey] = limits.CheckinIntervalMinutes,
                [DeviceTaskQueue.TaskLimitKey] = limits.MaxConcurrentTasks,
                [DeviceTaskQueue.CheckinLimitKey] = limits.MaxConcurrentCheckins
            };
            foreach (var entry in settings)
            {
                var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == entry.Key);
                if (setting is null)
                    db.AppSettings.Add(new AppSetting
                    {
                        Name = entry.Key,
                        Value = entry.Value.ToString()
                    });
                else
                    setting.Value = entry.Value.ToString();
            }

            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                AuditType = AuditType.Update,
                ObjectType = "DeviceTaskLimits",
                ObjectName = "Agent settings",
                ObjectJson = JsonSerializer.Serialize(limits)
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult.Ok();
        });
    }
}
