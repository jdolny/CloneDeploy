using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Gathers device totals, recent task results, and scheduled activity for the dashboard.
/// </summary>
public sealed class DashboardQueries(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    TimeProvider clock,
    StorageMountCoordinator? storage = null,
    AgentReleaseService? releases = null,
    CertificateExpiryNotices? certificateNotices = null)
{
    public async Task<ServiceResult<DashboardSnapshotDto>> GetAsync(int days)
    {
        days = days == 30 ? 30 : 7;
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) return ServiceResult<DashboardSnapshotDto>.Fail("Dashboard.Forbidden");
        var result = new DashboardSnapshotDto { AsOfUtc = clock.GetUtcNow().UtcDateTime, Days = days };
        result.CanViewDevices = (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded;
        result.CanApprove = (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceApprove)).Succeeded;
        result.IsAdmin = (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
        var taskAccess = await TaskAccess.ReadAsync(authentication, authorization);
        result.CanViewTasks = taskAccess is not null;
        result.CanViewAllTasks = taskAccess?.ViewAll == true;
        result.TaskUserId = taskAccess?.UserId;
        result.CanViewUpdates = (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportWindowsUpdates)).Succeeded;
        await using var db = await factory.CreateContextAsync();
        if (result.CanViewDevices)
        {
            var hour = result.AsOfUtc.AddHours(-1);
            var day = result.AsOfUtc.AddDays(-1);
            var week = result.AsOfUtc.AddDays(-7);
            var counts = await db.Devices.Where(x => x.ArchivedAtUtc == null).GroupBy(x => 1).Select(g => new
            {
                Total = g.Count(x => x.LastCheckinAtUtc != null), Recent = g.Count(x => x.LastCheckinAtUtc >= hour),
                Day = g.Count(x => x.LastCheckinAtUtc < hour && x.LastCheckinAtUtc >= day),
                Week = g.Count(x => x.LastCheckinAtUtc < day && x.LastCheckinAtUtc >= week),
                Older = g.Count(x => x.LastCheckinAtUtc < week), Never = g.Count(x => x.LastCheckinAtUtc == null)
            }).SingleOrDefaultAsync();
            result.DeviceCount = counts?.Total ?? 0;
            result.OverdueCount = (counts?.Week ?? 0) + (counts?.Older ?? 0);
            result.DeviceAges =
            [
                new("recent", counts?.Recent ?? 0), new("day", counts?.Day ?? 0),
                new("week", counts?.Week ?? 0), new("older", counts?.Older ?? 0), new("never", counts?.Never ?? 0)
            ];
        }

        if (result.CanApprove) result.PendingApprovals = await db.AgentProvisioningRequests.CountAsync(x => x.State == AgentProvisioningState.PendingApproval);
        if (taskAccess is not null)
        {
            var now = result.AsOfUtc;
            var yesterday = now.AddHours(-24);
            result.FailedCount = await db.DeviceTasks.CountAsync(x => (taskAccess.ViewAll || x.Deployment.RequestedByUserId == taskAccess.UserId) && x.Status == DeviceTaskStatus.Failed && x.FinishedAtUtc >= yesterday && x.FinishedAtUtc <= now);
            var start = now.Date.AddDays(1 - days);
            var daily = await db.DeviceTasks.Where(x => (taskAccess.ViewAll || x.Deployment.RequestedByUserId == taskAccess.UserId) && x.FinishedAtUtc >= start && x.FinishedAtUtc <= now &&
                                                        (x.Status == DeviceTaskStatus.Succeeded || x.Status == DeviceTaskStatus.Failed))
                .GroupBy(x => x.FinishedAtUtc!.Value.Date)
                .Select(g => new DashboardDayDto(g.Key, g.Count(x => x.Status == DeviceTaskStatus.Succeeded), g.Count(x => x.Status == DeviceTaskStatus.Failed)))
                .ToListAsync();
            result.Results = Enumerable.Range(0, days).Select(i => daily.SingleOrDefault(x => x.Date == start.AddDays(i)) ?? new DashboardDayDto(start.AddDays(i), 0, 0)).ToList();
            result.Deployments = await db.ActionDeployments.AsNoTracking().Where(x => (taskAccess.ViewAll || x.RequestedByUserId == taskAccess.UserId) && x.CreatedAtUtc >= start && x.CreatedAtUtc <= now)
                .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(10)
                .Select(x => new DashboardDeploymentDto(x.Id, x.ActionName, x.RequestedBy, x.CreatedAtUtc, x.Tasks.Count,
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Succeeded), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Failed),
                    x.Tasks.Count(t => t.Status == DeviceTaskStatus.Cancelled), x.Tasks.Count(t => t.Status == DeviceTaskStatus.Unknown)))
                .ToListAsync();
        }
        if (result.IsAdmin)
        {
            result.Scheduler = await db.ScheduledTasks.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new DashboardScheduledTaskDto(x.Id, x.IsEnabled,
                    x.History.OrderByDescending(r => r.QueuedAtUtc).ThenByDescending(r => r.Id).Select(r => (ScheduledTaskStatus?)r.Status).FirstOrDefault(),
                    x.History.OrderByDescending(r => r.QueuedAtUtc).ThenByDescending(r => r.Id).Select(r => (DateTime?)r.QueuedAtUtc).FirstOrDefault(),
                    x.History.Where(r => r.Status == ScheduledTaskStatus.Succeeded).Max(r => r.FinishedAtUtc)))
                .ToListAsync();
        }

        var release = (result.IsAdmin || result.CanViewDevices) && releases is not null ? await releases.ReadinessAsync(default) : null;
        if (result.CanViewDevices || result.CanViewUpdates)
        {
            var fleet = await DashboardFleetQueries.ReadAsync(db, result.AsOfUtc, release?.PreparedVersion);
            result.Fleet = fleet with { Updates = result.CanViewUpdates ? fleet.Updates : [], Agents = result.CanViewDevices ? fleet.Agents : [], PreparedVersion = result.CanViewDevices ? fleet.PreparedVersion : null };
        }
        await DashboardAttentionQueries.PopulateAsync(db, result,
            result.IsAdmin ? storage?.GetStatuses() ?? [] : [],
            release, result.IsAdmin && certificateNotices is not null
                ? await certificateNotices.ReadAsync(db, result.AsOfUtc, includeManaged: false) : []);
        return ServiceResult<DashboardSnapshotDto>.Ok(result);
    }

    public async Task<ServiceResult<List<DashboardDeviceDto>>> GetDevicesAsync(string bucket, DateTime asOf)
    {
        if (!await CanViewAsync(Constants.Permissions.DeviceView))
            return ServiceResult<List<DashboardDeviceDto>>.Fail("Dashboard.Forbidden");
        if (bucket is not ("recent" or "day" or "week" or "older" or "never" or "overdue" or "all") ||
            asOf.Kind != DateTimeKind.Utc || asOf < DateTime.SpecifyKind(DateTime.MinValue.AddDays(7), DateTimeKind.Utc))
            return ServiceResult<List<DashboardDeviceDto>>.Fail("Dashboard.Validation");
        await using var db = await factory.CreateContextAsync();
        var query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
        var hour = asOf.AddHours(-1);
        var day = asOf.AddDays(-1);
        var week = asOf.AddDays(-7);
        query = bucket switch
        {
            "recent" => query.Where(x => x.LastCheckinAtUtc >= hour),
            "day" => query.Where(x => x.LastCheckinAtUtc < hour && x.LastCheckinAtUtc >= day),
            "week" => query.Where(x => x.LastCheckinAtUtc < day && x.LastCheckinAtUtc >= week),
            "older" => query.Where(x => x.LastCheckinAtUtc < week),
            "never" => query.Where(x => x.LastCheckinAtUtc == null),
            "overdue" => query.Where(x => x.LastCheckinAtUtc != null && x.LastCheckinAtUtc < day),
            "all" => query.Where(x => x.LastCheckinAtUtc != null),
            _ => query
        };
        return ServiceResult<List<DashboardDeviceDto>>.Ok(await query.OrderBy(x => x.LastCheckinAtUtc).ThenBy(x => x.Name).ThenBy(x => x.Id).Take(200)
            .Select(x => new DashboardDeviceDto(x.Id, x.Name, x.LastCheckinAtUtc, x.ClientVersion)).ToListAsync());
    }

    public async Task<ServiceResult<List<DashboardTaskDto>>> GetTasksAsync(DeviceTaskStatus status, DateTime from, DateTime to)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization);
        if (access is null)
            return ServiceResult<List<DashboardTaskDto>>.Fail("Dashboard.Forbidden");
        if (!System.Enum.IsDefined(status) || from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from > to)
            return ServiceResult<List<DashboardTaskDto>>.Fail("Dashboard.Validation");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<DashboardTaskDto>>.Ok(await db.DeviceTasks.AsNoTracking().Where(x => (access.ViewAll || x.Deployment.RequestedByUserId == access.UserId) && x.Status == status && x.FinishedAtUtc >= from && x.FinishedAtUtc <= to)
            .OrderByDescending(x => x.FinishedAtUtc).ThenBy(x => x.Id).Take(200)
            .Select(x => new DashboardTaskDto(x.DeploymentId, x.Deployment.ActionName, x.DeviceName, x.Status, x.FinishedAtUtc, x.Result)).ToListAsync());
    }

    public async Task<ServiceResult<List<DashboardFleetDeviceDto>>> GetFleetDevicesAsync(string bucket, DateTime asOf, string? version)
    {
        if (!await CanViewAsync(DashboardFleetQueries.UpdateBuckets.Contains(bucket) ? Constants.Permissions.ReportWindowsUpdates : Constants.Permissions.DeviceView))
            return ServiceResult<List<DashboardFleetDeviceDto>>.Fail("Dashboard.Forbidden");
        if (asOf.Kind != DateTimeKind.Utc || asOf < new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc) ||
            (!DashboardFleetQueries.UpdateBuckets.Contains(bucket) && !DashboardFleetQueries.AgentBuckets.Contains(bucket)) ||
            (DashboardFleetQueries.AgentBuckets.Contains(bucket) && DashboardFleetQueries.VersionOf(version) is null))
            return ServiceResult<List<DashboardFleetDeviceDto>>.Fail("Dashboard.Validation");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<DashboardFleetDeviceDto>>.Ok(await DashboardFleetQueries.DevicesAsync(db, bucket, asOf, version));
    }

    private async Task<bool> CanViewAsync(string policy)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && (await authorization.AuthorizeAsync(user, policy)).Succeeded;
    }
}
