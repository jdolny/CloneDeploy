using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Runs directory synchronization when requested by the scheduler.
/// </summary>
public sealed class DirectorySyncTaskHandler(ICloneDeployDbFactory dbFactory, DirectorySyncRunner runner) : IScheduledTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.DirectorySync;
    public string Name => "Sync Directories";
    public string Description => "Synchronizes every enabled Active Directory and EntraID connection";

    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateContextAsync();

        return await db.DirectorySources.Where(x => x.IsEnabled)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }

    public async Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateContextAsync();
        return await db.DirectorySources.Where(x => x.Id == targetId && x.IsEnabled).Select(x => x.Name).SingleOrDefaultAsync(ct);
    }
    
    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(targetId, ct);
        // The scheduler's Task-only handler contract records exceptions as failed runs.
        if (outcome.Summary is null)
            throw new InvalidOperationException(outcome.Error);
    }
}