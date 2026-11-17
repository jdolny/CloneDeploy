using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class GroupPowerScheduleService(ICloneDeployDbFactory factory, IAuthorizationService authorization,
    ScheduleCalculator calculator, TimeProvider clock)
{
    private async Task<int> Authorize(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceGroupPowerManagement)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            throw new UnauthorizedAccessException();
        return id;
    }

    public async Task<List<DeviceGroupPowerSchedule>> GetAsync(ClaimsPrincipal user, int groupId)
    {
        await Authorize(user);
        await using var db = await factory.CreateContextAsync();
        return await db.DeviceGroupPowerSchedules.AsNoTracking().Include(x => x.Schedule)
            .Where(x => x.DeviceGroupId == groupId).OrderBy(x => x.Action).ThenBy(x => x.Schedule.Name).ToListAsync();
    }

    public async Task SaveAsync(ClaimsPrincipal user, DeviceGroupPowerSchedule input)
    {
        var userId = await Authorize(user);
        if (!System.Enum.IsDefined(input.Action)) throw new InvalidOperationException("Select a valid power action.");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Shared with execution so an assignment is either changed before the claim or after it.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947239, 1)");
        if (!await db.DeviceGroups.AnyAsync(x => x.Id == input.DeviceGroupId))
            throw new InvalidOperationException("The device group no longer exists.");
        var schedule = await db.Schedules.FindAsync(input.ScheduleId);
        if (schedule is null || !calculator.IsValid(schedule)) throw new InvalidOperationException("Select a valid schedule.");
        if (await db.DeviceGroupPowerSchedules.AnyAsync(x => x.Id != input.Id && x.DeviceGroupId == input.DeviceGroupId &&
                x.ScheduleId == input.ScheduleId && x.Action == input.Action))
            throw new InvalidOperationException("This action and schedule are already assigned to the group.");
        var row = input.Id == 0 ? new DeviceGroupPowerSchedule { DeviceGroupId = input.DeviceGroupId } :
            await db.DeviceGroupPowerSchedules.SingleOrDefaultAsync(x => x.Id == input.Id && x.DeviceGroupId == input.DeviceGroupId)
                ?? throw new InvalidOperationException("This assignment no longer exists.");
        if (input.Id == 0) db.Add(row);
        row.Action = input.Action;
        row.ScheduleId = input.ScheduleId;
        row.Enabled = input.Enabled;
        row.ConfiguredByUserId = userId;
        row.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.LastResult = null;
        row.LastRunAtUtc = null;
        await db.SaveChangesAsync();
        db.AuditLogs.Add(Audit(user, userId, row, AuditType.Update));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task DeleteAsync(ClaimsPrincipal user, int groupId, int id)
    {
        var userId = await Authorize(user);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947239, 1)");
        var row = await db.DeviceGroupPowerSchedules.SingleOrDefaultAsync(x => x.Id == id && x.DeviceGroupId == groupId);
        if (row is null) return;
        db.AuditLogs.Add(Audit(user, userId, row, AuditType.Delete));
        db.Remove(row);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private AuditLog Audit(ClaimsPrincipal user, int userId, DeviceGroupPowerSchedule row, AuditType type) => new()
    {
        AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = type,
        ObjectType = "DeviceGroup", ObjectId = row.DeviceGroupId, ObjectName = "Power schedule",
        LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
        ObjectJson = JsonSerializer.Serialize(new { Section = $"Power schedule: {row.Action}, schedule #{row.ScheduleId}, {(row.Enabled ? "enabled" : "disabled")}", row.Id, row.ScheduleId, row.Action, row.Enabled })
    };
}
