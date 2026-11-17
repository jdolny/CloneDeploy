using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

// Targets are schedule IDs, allowing every action type to use its shared ActionReference.
/// <summary>
/// Finds and queues action files that are due to be downloaded again.
/// </summary>
public sealed class ExternalFileRefreshTaskHandler(ICloneDeployDbFactory factory, ScheduleCalculator schedules,
    TimeProvider clock, StoredFileStorage storage) : IScheduledTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.ExternalFileRefresh;
    public string Name => "Check external file schedules";
    public string Description => "Queues changed-file checks when action schedules are due. Pinned and opted-out files are skipped.";

    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.ActionReferences.Where(x => x.ArchivedAtUtc == null && x.ExternalFileUpdateScheduleId != null)
            .Select(x => x.ExternalFileUpdateScheduleId!.Value).Distinct().OrderBy(x => x).ToListAsync(ct);
    }

    public async Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.Schedules.Where(x => x.Id == targetId).Select(x => x.Name).SingleOrDefaultAsync(ct);
    }

    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        await storage.Gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateContextAsync();
            var schedule = await db.Schedules.FindAsync([targetId], ct);
            if (schedule is null || !schedules.IsValid(schedule)) return;
            var now = clock.GetUtcNow().UtcDateTime;
            var actions = await db.ActionReferences.Where(x => x.ArchivedAtUtc == null && x.ExternalFileUpdateScheduleId == targetId).ToListAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var action in actions)
            {
                var after = action.ExternalFilesLastEvaluatedAtUtc;
                if (after > now) continue;
                var due = after.HasValue ? schedules.GetNextScheduledTime(schedule, after.Value) : null;
                if (due.HasValue && due <= now)
                {
                    var ids = db.ActionFiles.Where(x => x.ActionGuid == action.ActionGuid).Select(x => x.StoredFileId);
                    // Conditional update cannot reset a download already claimed by the worker.
                    await db.StoredFiles.Where(x => ids.Contains(x.Id) && x.SourceType == FileSourceType.Url &&
                            x.AutoRefresh && x.ExpectedSha256Hash == null &&
                            (x.Status == StoredFileStatus.Complete || x.Status == StoredFileStatus.Error))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StoredFileStatus.Queued)
                            .SetProperty(x => x.ErrorMessage, (string?)null).SetProperty(x => x.Progress, (string?)null), ct);
                }
                action.ExternalFilesLastEvaluatedAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally { storage.Gate.Release(); }
    }
}
