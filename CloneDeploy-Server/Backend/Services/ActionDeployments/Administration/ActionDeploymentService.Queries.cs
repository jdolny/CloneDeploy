using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides lists and status details for reviewing action deployments.
/// </summary>
public sealed partial class ActionDeploymentService
{
    public Task<ServiceResult<List<DeviceActionHistoryRowDto>>> GetDeviceHistoryAsync(int deviceId) => ExecuteAsync(async () =>
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<DeviceActionHistoryRowDto>>.Fail(ErrorKeys.Deployment.Forbidden);

        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<DeviceActionHistoryRowDto>>.Ok(await db.DeviceTasks.AsNoTracking()
            .Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.Deployment.CreatedAtUtc).ThenByDescending(x => x.Id).Take(100)
            .Select(x => new DeviceActionHistoryRowDto(x.Id, x.Deployment.ActionName, x.Deployment.RequestedBy,
                x.Deployment.Source, x.Status, x.Deployment.CreatedAtUtc, x.StartedAtUtc, x.FinishedAtUtc, x.Result))
            .ToListAsync());
    });

    public async Task<ServiceResult<string>> GetActionNameAsync(Guid actionGuid)
    {
        return await ExecuteAsync(async () =>
        {
            var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ActionDeploy);
            if (user is null)
                return ServiceResult<string>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            var (action, error) = await PolicyCompiler.CompileStandaloneAsync(db, actionGuid);
            return error is null
                ? ServiceResult<string>.Ok(action!.Name)
                : ServiceResult<string>.Fail(ErrorKeys.Deployment.Validation, error);
        });
    }

    public async Task<ServiceResult<DeploymentCatalogDto>> GetCatalogAsync()
    {
        return await ExecuteAsync(async () =>
        {
            var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ActionDeploy)
                ?? await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ReportWindowsUpdates);
            if (user is null)
                return ServiceResult<DeploymentCatalogDto>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            return ServiceResult<DeploymentCatalogDto>.Ok(await DeploymentTargets.ReadCatalogAsync(db));
        });
    }

    public async Task<ServiceResult<List<PolicyTargetDeviceDto>>> SearchDevicesAsync(string? search)
    {
        return await ExecuteAsync(async () =>
        {
            var user = await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ActionDeploy)
                ?? await PolicyActionAccess.UserAsync(authentication, authorization, Constants.Permissions.ReportWindowsUpdates);
            if (user is null)
                return ServiceResult<List<PolicyTargetDeviceDto>>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            var query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var text = search.Trim().ToLower();
                query = query.Where(x => x.Name.ToLower().Contains(text));
            }

            return ServiceResult<List<PolicyTargetDeviceDto>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100)
                .Select(x => new PolicyTargetDeviceDto(x.Id, x.Name)).ToListAsync());
        });
    }

    public Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync() => GetDeploymentsAsync(null);

    public Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync(bool? history) => GetDeploymentsAsync(history, false);

    public async Task<ServiceResult<List<DeploymentRowDto>>> GetDeploymentsAsync(bool? history, bool allTasks)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            if (access is null)
                return ServiceResult<List<DeploymentRowDto>>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            if (allTasks && !access.ViewAll) return ServiceResult<List<DeploymentRowDto>>.Fail(ErrorKeys.Deployment.Forbidden);
            var query = db.ActionDeployments.AsNoTracking().Where(x => allTasks || x.RequestedByUserId == access.UserId);
            if (history is bool showHistory)
                query = query.Where(x => x.Tasks.Any(t => t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Offered ||
                                                          t.Status == DeviceTaskStatus.Running || t.Status == DeviceTaskStatus.Unknown) != showHistory);
            return ServiceResult<List<DeploymentRowDto>>.Ok(await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(200)
                .Select(x => new DeploymentRowDto(x.Id, x.ActionName, x.RequestedBy, x.CreatedAtUtc, x.Timing, x.Tasks.Count,
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Queued), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Offered || t.Status == DeviceTaskStatus.Running),
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Succeeded), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Failed))
                {
                    Cancelled = x.Tasks.Count(t => t.Status == DeviceTaskStatus.Cancelled),
                    Unknown = x.Tasks.Count(t => t.Status == DeviceTaskStatus.Unknown),
                    Source = x.Source
                }).ToListAsync());
        });
    }

    public Task<ServiceResult<List<DeploymentRowDto>>> GetUnknownDeploymentsAsync() => GetUnknownDeploymentsAsync(false);

    public async Task<ServiceResult<List<DeploymentRowDto>>> GetUnknownDeploymentsAsync(bool allTasks)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            if (access is null || allTasks && !access.ViewAll) return ServiceResult<List<DeploymentRowDto>>.Fail(ErrorKeys.Deployment.Forbidden);
            await using var db = await factory.CreateContextAsync();
            return ServiceResult<List<DeploymentRowDto>>.Ok(await db.ActionDeployments.AsNoTracking()
                .Where(x => (allTasks || x.RequestedByUserId == access.UserId) && x.Tasks.Any(t => t.Status == DeviceTaskStatus.Unknown))
                .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(200)
                .Select(x => new DeploymentRowDto(x.Id, x.ActionName, x.RequestedBy, x.CreatedAtUtc, x.Timing, x.Tasks.Count,
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Queued), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Offered || t.Status == DeviceTaskStatus.Running),
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Succeeded), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Failed))
                { Cancelled = x.Tasks.Count(t => t.Status == DeviceTaskStatus.Cancelled), Unknown = x.Tasks.Count(t => t.Status == DeviceTaskStatus.Unknown), Source = x.Source })
                .ToListAsync());
        });
    }

    public async Task<ServiceResult<List<DeviceTask>>> GetTasksAsync(Guid deploymentId)
    {
        return await ExecuteAsync(async () =>
        {
            var access = await TaskAccess.ReadAsync(authentication, authorization);
            if (access is null)
                return ServiceResult<List<DeviceTask>>.Fail(ErrorKeys.Deployment.Forbidden);

            await using var db = await factory.CreateContextAsync();
            return ServiceResult<List<DeviceTask>>.Ok(await db.DeviceTasks.AsNoTracking().Include(x => x.Deployment).Where(x => x.DeploymentId == deploymentId && (access.ViewAll || x.Deployment.RequestedByUserId == access.UserId))
                .OrderBy(x => x.DeviceName).ToListAsync());
        });
    }
}
