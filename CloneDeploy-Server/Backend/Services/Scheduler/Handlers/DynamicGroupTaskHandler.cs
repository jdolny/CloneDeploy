using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.Scheduler;

/// <summary>
/// Refreshes automatic device group membership when requested by the scheduler.
/// </summary>
public sealed class DynamicGroupTaskHandler(ICloneDeployDbFactory dbFactory, DynamicDeviceGroupRunner runner) : IScheduledBatchTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.DynamicGroupRecalculation;
    public string Name => "Recalculate Dynamic Groups";
    public string Description => "Recalculates the members for every saved dynamic device group";

    public async Task<IReadOnlyDictionary<int, string?>> RunAllAsync(CancellationToken ct) =>
        (await runner.RunAllAsync(ct)).ToDictionary(x => x.Key, x => x.Value.Error);
    
    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateContextAsync();

        return await db.DeviceGroups.Where(x => x.Type == DeviceGroupType.Dynamic && x.QueryDefinitionJson != null)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
    }
    
    public async Task<string?> GetTargetNameAsync(int targetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateContextAsync();

        return await db.DeviceGroups.Where(x => x.Id == targetId && x.Type == DeviceGroupType.Dynamic).Select(x => x.Name).SingleOrDefaultAsync(ct);
    }

    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(targetId, ct);
        // The scheduler's Task-only handler contract records exceptions as failed runs.
        if (outcome.Result is null)
            throw new InvalidOperationException(outcome.Error);
    }
}
