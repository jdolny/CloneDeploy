using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

public sealed class GroupPowerScheduleTaskHandler(ICloneDeployDbFactory factory, ScheduleCalculator calculator,
    TimeProvider clock, IScheduledPowerActions actions, ILogger<GroupPowerScheduleTaskHandler> logger) : IScheduledBatchTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.GroupPowerSchedules;
    public string Name => "Check device group power schedules";
    public string Description => "Checks assigned wake-up and shutdown schedules. Offline shutdowns and occurrences more than ten minutes late are skipped.";

    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.DeviceGroupPowerSchedules.Where(x => x.Enabled).Select(x => x.DeviceGroupId).Distinct().ToListAsync(ct);
    }
    public async Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.DeviceGroups.Where(x => x.Id == targetId).Select(x => x.Name).SingleOrDefaultAsync(ct);
    }
    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        var results = await RunDueAsync(targetId, ct);
        if (results.Values.Any(x => x is not null)) throw new InvalidOperationException("Some power schedule actions were skipped or failed. See the group's power schedule results.");
    }
    public Task<IReadOnlyDictionary<int, string?>> RunAllAsync(CancellationToken ct) => RunDueAsync(null, ct);

    private sealed record Work(DeviceGroupPowerSchedule Assignment, DateTime Due, int[] Devices);

    private async Task<IReadOnlyDictionary<int, string?>> RunDueAsync(int? groupId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var work = new List<Work>();
        var results = new Dictionary<int, string?>();
        await using (var db = await factory.CreateContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947239, 1)", ct);
            var assignments = await db.DeviceGroupPowerSchedules.Include(x => x.Schedule)
                .Where(x => x.Enabled && (groupId == null || x.DeviceGroupId == groupId)).OrderBy(x => x.Id).ToListAsync(ct);
            foreach (var row in assignments)
            {
                if (row.LastEvaluatedAtUtc >= now) continue;
                var due = calculator.GetNextScheduledTime(row.Schedule, row.LastEvaluatedAtUtc);
                row.LastEvaluatedAtUtc = now;
                if (due is null || due > now) continue;
                row.LastRunAtUtc = now;
                if (now - due.Value > TimeSpan.FromMinutes(10))
                {
                    row.LastResult = "Skipped missed occurrence: more than ten minutes late.";
                    results[row.Id] = row.LastResult;
                    continue;
                }
                var members = DeviceGroupMembershipQuery.DeviceIds(db, [row.DeviceGroupId]);
                var devices = await db.Devices.Where(x => members.Contains(x.Id) && x.ArchivedAtUtc == null &&
                    x.ProvisionStatus != DeviceProvisionStatus.Archived).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
                // Commit the occurrence before any external side effect. A crash must never replay a shutdown.
                row.LastResult = "Claimed for execution. If interrupted, this occurrence will not be retried.";
                work.Add(new(row, due.Value, devices));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var conflicts = work.SelectMany(x => x.Devices.Select(id => (Id: id, x.Assignment.Action)))
            .GroupBy(x => x.Id).Where(x => x.Select(y => y.Action).Distinct().Count() > 1).Select(x => x.Key).ToHashSet();
        var dispatched = new Dictionary<(DevicePowerAction, int), string?>();
        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();
            var row = item.Assignment;
            var failures = new List<string>();
            var succeeded = 0;
            if (!await actions.CanRunAsync(row.ConfiguredByUserId))
                failures.Add("The user who configured this assignment no longer has group power management access. Save it again with an authorized user.");
            else
            {
                if (row.Action == DevicePowerAction.Wake && clock.GetUtcNow().UtcDateTime - item.Due <= TimeSpan.FromMinutes(10))
                {
                    var pending = item.Devices.Where(id => !conflicts.Contains(id) && !dispatched.ContainsKey((row.Action, id))).ToArray();
                    if (pending.Length > 0)
                    {
                        try
                        {
                            var wakeResults = await actions.WakeAsync(row.ConfiguredByUserId, pending, ct);
                            foreach (var id in pending) dispatched[(row.Action, id)] = wakeResults.TryGetValue(id, out var error) ? error : "Wake-up was not confirmed.";
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Scheduled wake-up failed for assignment {AssignmentId}", row.Id);
                            foreach (var id in pending) dispatched[(row.Action, id)] = "Wake-up could not be completed.";
                        }
                    }
                }
                foreach (var id in item.Devices)
                {
                    ct.ThrowIfCancellationRequested();
                    string? error;
                    if (clock.GetUtcNow().UtcDateTime - item.Due > TimeSpan.FromMinutes(10))
                        error = "The occurrence expired before dispatch.";
                    else if (conflicts.Contains(id))
                        error = "Conflicting wake-up and shutdown schedules were due for this device.";
                    else if (!dispatched.TryGetValue((row.Action, id), out error))
                    {
                        try { error = await actions.RunAsync(row.ConfiguredByUserId, row.Action, id, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Scheduled power action failed for device {DeviceId}", id);
                            error = "The agent operation could not be completed.";
                        }
                        dispatched[(row.Action, id)] = error;
                    }
                    if (error is null) succeeded++;
                    else failures.Add(error);
                }
            }
            var summary = $"{succeeded} of {item.Devices.Length} device(s) confirmed. " +
                string.Join(" ", failures.Distinct());
            if (summary.Length > 1024) summary = summary[..1024];
            results[row.Id] = failures.Count == 0 ? null : summary;
            await using var update = await factory.CreateContextAsync();
            // Do not overwrite a newer edit or an assignment removed while this run was in progress.
            await update.DeviceGroupPowerSchedules.Where(x => x.Id == row.Id && x.LastEvaluatedAtUtc == now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastResult, summary), ct);
            if (await update.Users.AnyAsync(x => x.Id == row.ConfiguredByUserId, ct))
            {
                update.AuditLogs.Add(new AuditLog
                {
                    AppUserId = row.ConfiguredByUserId, UserName = "Power scheduler", AuditType = AuditType.Update,
                    ObjectType = "DeviceGroup", ObjectId = row.DeviceGroupId, ObjectName = "Scheduled " + row.Action,
                    LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
                    ObjectJson = JsonSerializer.Serialize(new { Section = summary, AssignmentId = row.Id, item.Due, row.Action, Summary = summary })
                });
                await update.SaveChangesAsync(ct);
            }
        }
        return results;
    }
}
