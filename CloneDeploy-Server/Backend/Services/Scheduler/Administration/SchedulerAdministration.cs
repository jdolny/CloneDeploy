using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Manages the available background tasks and their scheduling settings.
/// </summary>
public sealed class SchedulerAdministration(
    ICloneDeployDbFactory dbFactory,
    ScheduleCalculator scheduleService,
    TimeProvider clock,
    SchedulerGate gate,
    IEnumerable<IScheduledTaskHandler> handlers)
{
    private readonly Dictionary<ScheduledTaskType, IScheduledTaskHandler> _handlers = handlers.ToDictionary(x => x.Type);

    public async Task InitializeScheduledTasksAsync()
    {
        await using var db = await dbFactory.CreateContextAsync();

        var existing = await db.ScheduledTasks.Select(x => x.Id).ToListAsync();

        var missing = _handlers.Keys.Except(existing).Select(x => new ScheduledTask { Id = x });

        db.ScheduledTasks.AddRange(missing);

        // The task checks action and driver schedules; it does not download every file on each run.
        var refresh = db.ChangeTracker.Entries<ScheduledTask>()
            .Select(x => x.Entity).SingleOrDefault(x => x.Id == ScheduledTaskType.ExternalFileRefresh);
        if (refresh is not null)
        {
            var scheduleName = "External file schedule checks";
            for (var suffix = 2; await db.Schedules.AnyAsync(x => x.Name == scheduleName); suffix++)
                scheduleName = $"External file schedule checks ({suffix})";
            refresh.IsEnabled = true;
            refresh.Schedule = new Schedule
            {
                Name = scheduleName,
                Description = "Checks which action and driver file refresh schedules are due.",
                Type = ScheduleType.Interval, IntervalMinutes = 5,
                StartsAtUtc = clock.GetUtcNow().UtcDateTime
            };
            refresh.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        // Seed only new tasks; restarting must preserve administrator scheduling choices.
        var approvals = db.ChangeTracker.Entries<ScheduledTask>()
            .Select(x => x.Entity).SingleOrDefault(x => x.Id == ScheduledTaskType.WindowsUpdateAutoApproval);
        if (approvals is not null)
        {
            var scheduleName = "Windows Update auto-approval";
            for (var suffix = 2; await db.Schedules.AnyAsync(x => x.Name == scheduleName); suffix++)
                scheduleName = $"Windows Update auto-approval ({suffix})";
            approvals.IsEnabled = true;
            approvals.Schedule = new Schedule
            {
                Name = scheduleName,
                Description = "Evaluates enabled Windows Update auto-approval rules.",
                Type = ScheduleType.Interval, IntervalMinutes = 5,
                StartsAtUtc = clock.GetUtcNow().UtcDateTime
            };
            approvals.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        var emails = db.ChangeTracker.Entries<ScheduledTask>().Select(x => x.Entity).SingleOrDefault(x => x.Id == ScheduledTaskType.EmailDigests);
        if (emails is not null)
        {
            var name = "Email digest checks";
            for (var suffix = 2; await db.Schedules.AnyAsync(x => x.Name == name); suffix++) name = $"Email digest checks ({suffix})";
            emails.IsEnabled = true;
            emails.Schedule = new Schedule { Name = name, Description = "Checks user email subscriptions; delivery follows each user's preferences.",
                Type = ScheduleType.Interval, IntervalMinutes = 5, StartsAtUtc = clock.GetUtcNow().UtcDateTime };
            emails.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        var power = db.ChangeTracker.Entries<ScheduledTask>().Select(x => x.Entity).SingleOrDefault(x => x.Id == ScheduledTaskType.GroupPowerSchedules);
        if (power is not null)
        {
            var name = "Device group power schedule checks";
            for (var suffix = 2; await db.Schedules.AnyAsync(x => x.Name == name); suffix++) name = $"Device group power schedule checks ({suffix})";
            power.IsEnabled = true;
            power.Schedule = new Schedule { Name = name, Description = "Checks assigned wake-up and shutdown schedules.",
                Type = ScheduleType.Interval, IntervalMinutes = 5, StartsAtUtc = clock.GetUtcNow().UtcDateTime };
            power.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync();
    }

    public async Task<ServiceResult<List<SchedulerTaskDto>>> GetScheduledTaskViews(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateContextAsync();
        var scheduledTasks = await db.ScheduledTasks.AsNoTracking().Include(x => x.Schedule).Include(x => x.History.OrderByDescending(h => h.Id).Take(10))
            .OrderBy(x => x.Id).ToListAsync(ct);

        var scheduledTaskViews = new List<SchedulerTaskDto>();
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        foreach (var task in scheduledTasks)
        {
            var lastRun = task.History.OrderByDescending(x => x.Status == ScheduledTaskStatus.Running).ThenByDescending(x => x.Id).FirstOrDefault();
            var nextRun = task is { IsEnabled: true, Schedule: not null } ? scheduleService.GetNextScheduledTime(task.Schedule, nowUtc) : null;

            _handlers.TryGetValue(task.Id, out var handler);

            scheduledTaskViews.Add(new SchedulerTaskDto
            {
                Id = task.Id,
                Name = handler?.Name ?? task.Id.ToString(),
                Description = handler?.Description ?? string.Empty,
                Enabled = task.IsEnabled,
                ScheduleId = task.ScheduleId,
                NextRunUtc = nextRun,
                LastRun = lastRun,
                RecentRuns = task.History.OrderByDescending(x => x.Id).ToList(),
            });
        }

        return ServiceResult<List<SchedulerTaskDto>>.Ok(scheduledTaskViews);
    }

    public async Task<ServiceResult<ScheduledTask>> SaveAsync(ScheduledTaskType id, int? scheduleId, bool enabled, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateContextAsync();

            var task = await db.ScheduledTasks.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (task is null)
                return ServiceResult<ScheduledTask>.Fail(ErrorKeys.Common.NotFound, id);
            if (!_handlers.ContainsKey(id))
                return ServiceResult<ScheduledTask>.Fail(ErrorKeys.Scheduler.HandlerNotFound, id);
            var schedule = scheduleId.HasValue ? await db.Schedules.FindAsync([scheduleId.Value], ct) : null;

            if ((scheduleId.HasValue && schedule is null) || (enabled && (schedule is null || !scheduleService.IsValid(schedule))))
                return ServiceResult<ScheduledTask>.Fail(ErrorKeys.Scheduler.InvalidSchedule);

            if (task.IsEnabled != enabled || task.ScheduleId != scheduleId)
            {
                // Configuration changes apply only to future occurrences.
                task.LastEvaluatedAtUtc = clock.GetUtcNow().UtcDateTime;
            }

            task.ScheduleId = scheduleId;
            task.IsEnabled = enabled;
            await db.SaveChangesAsync(ct);
            return ServiceResult<ScheduledTask>.Ok(task);
        }
        finally
        {
            gate.Release();
        }
    }
}
