using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks and saves schedules and prevents deletion while they are still in use.
/// </summary>
public sealed class ScheduleAdministration(
    ICloneDeployDbFactory dbFactory,
    ScheduleCalculator calculator,
    SharedResourceAccess access)
{
    public async Task<ServiceResult<List<Schedule>>> GetAsync(string? searchText = null)
    {
        if (!await access.AllowedAsync(Constants.Permissions.ScheduleView)) return ServiceResult<List<Schedule>>.Fail("SharedResource.Forbidden");
        await using var db = await dbFactory.CreateContextAsync();
        var query = db.Schedules.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<Schedule>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<Schedule>> CreateAsync(Schedule input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.ScheduleEdit)) return ServiceResult<Schedule>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !calculator.IsValid(input))
            return ServiceResult<Schedule>.Fail(ErrorKeys.Scheduler.InvalidSchedule);

        await using var db = await dbFactory.CreateContextAsync();
        if (await db.Schedules.AnyAsync(x => x.Name == name))
            return ServiceResult<Schedule>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new Schedule();
        CopySettings(input, entity, name);
        db.Schedules.Add(entity);
        await db.SaveChangesAsync();
        return ServiceResult<Schedule>.Ok(entity);
    }

    public async Task<ServiceResult<Schedule>> UpdateAsync(Schedule input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.ScheduleEdit)) return ServiceResult<Schedule>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !calculator.IsValid(input))
            return ServiceResult<Schedule>.Fail(ErrorKeys.Scheduler.InvalidSchedule);

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947239, 1)");
        var entity = await db.Schedules.FindAsync(input.Id);
        if (entity is null)
            return ServiceResult<Schedule>.Fail(ErrorKeys.Common.NotFound, input.Id);
        if (await db.Schedules.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult<Schedule>.Fail(ErrorKeys.Common.AlreadyExists, name);

        CopySettings(input, entity, name);
        await db.SaveChangesAsync();
        await db.DeviceGroupPowerSchedules.Where(x => x.ScheduleId == entity.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastEvaluatedAtUtc, DateTime.UtcNow));
        await transaction.CommitAsync();
        return ServiceResult<Schedule>.Ok(entity);
    }

    public Task<ServiceResult> DeleteAsync(int id) => DeleteManyAsync([id]);

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await access.AllowedAsync(Constants.Permissions.ScheduleEdit)) return ServiceResult.Fail("SharedResource.Forbidden");
        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0)
            return ServiceResult.Ok();

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entities = await db.Schedules.Where(x => selected.Contains(x.Id)).ToListAsync();
        if (entities.Count != selected.Length)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound);
        if (await IsInUseAsync(db, selected))
            return ServiceResult.Fail(ErrorKeys.Schedule.InUse);

        db.Schedules.RemoveRange(entities);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static async Task<bool> IsInUseAsync(CloneDeployDbContext db, int[] ids) =>
        await db.DeviceGroupPowerSchedules.AnyAsync(x => ids.Contains(x.ScheduleId)) ||
        await db.ScheduledTasks.AnyAsync(x => x.ScheduleId.HasValue && ids.Contains(x.ScheduleId.Value)) ||
        await db.DirectorySources.AnyAsync(x => x.ScheduleId.HasValue && ids.Contains(x.ScheduleId.Value)) ||
        await db.ActionReferences.AnyAsync(x => x.ExternalFileUpdateScheduleId.HasValue && ids.Contains(x.ExternalFileUpdateScheduleId.Value)) ||
        await db.DriverProfiles.AnyAsync(x => x.UpdateScheduleId.HasValue && ids.Contains(x.UpdateScheduleId.Value));

    private static void CopySettings(Schedule input, Schedule entity, string name)
    {
        entity.Name = name;
        entity.Description = input.Description?.Trim();
        entity.Type = input.Type;
        entity.IntervalMinutes = input.IntervalMinutes;
        entity.StartsAtUtc = input.StartsAtUtc;
        entity.Monday = input.Monday;
        entity.Tuesday = input.Tuesday;
        entity.Wednesday = input.Wednesday;
        entity.Thursday = input.Thursday;
        entity.Friday = input.Friday;
        entity.Saturday = input.Saturday;
        entity.Sunday = input.Sunday;
        entity.Time = input.Time;
    }

}
