using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

// Trusted background entry point; UI requests go through DynamicDeviceGroupService.
/// <summary>
/// Refreshes automatic device group membership using the saved matching rules.
/// </summary>
public sealed class DynamicDeviceGroupRunner(ICloneDeployDbFactory factory, ILogger<DynamicDeviceGroupRunner> logger)
{
    /// <summary>Evaluates each saved dynamic group once, after its dependencies.</summary>
    public async Task<IReadOnlyDictionary<int, DynamicGroupRebuildOutcomeDto>> RunAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(30);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        // Keep the graph and manual memberships stable for the entire pass.
        await DeviceGroupDependencies.LockAsync(db, ct);
        var groups = await db.DeviceGroups.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var targets = groups.Values.Where(x => x.Type == DeviceGroupType.Dynamic && x.QueryDefinitionJson != null)
            .OrderBy(x => x.Id).ToList();
        var outcomes = new Dictionary<int, DynamicGroupRebuildOutcomeDto>();
        var ordered = new List<DeviceGroup>();
        var included = new HashSet<int>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var dependency in DeviceGroupDependencies.Ordered(groups, target.Id))
                    if (included.Add(dependency.Id)) ordered.Add(dependency);
            }
            catch (InvalidOperationException exception)
            {
                outcomes[target.Id] = new(null, exception.Message);
            }
        }

        // Consistent lock order with directory membership invalidation.
        foreach (var id in ordered.Select(x => x.Id).Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + id}, 0))", ct);
        foreach (var group in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var definition = DeviceQueryDefinitionDto.Parse(group.QueryDefinitionJson!);
            var failedDependencies = DeviceGroupDependencies.References(definition)
                .Where(id => outcomes.TryGetValue(id, out var outcome) && outcome.Result is null).ToArray();
            if (failedDependencies.Length > 0)
            {
                outcomes[group.Id] = new(null, "Skipped because a dependency failed: " +
                    string.Join(", ", failedDependencies.Select(id => groups[id].Name)));
                continue;
            }

            // A failing parent must not discard successful dependencies or unrelated groups.
            await transaction.CreateSavepointAsync("dynamic_group", ct);
            try
            {
                var (query, error) = await DeviceQueryBuilder.BuildAsync(db, definition, ct);
                if (error is not null) throw new InvalidOperationException(error);
                db.Attach(group);
                var count = await ReplaceMembershipAsync(db, group, query!, ct);
                await db.SaveChangesAsync(ct);
                outcomes[group.Id] = new(new(group.Id, count, group.MembershipEvaluatedAtUtc!.Value), null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await transaction.RollbackToSavepointAsync("dynamic_group", ct);
                logger.LogError(exception, "Dynamic group rebuild failed for {GroupId}", group.Id);
                outcomes[group.Id] = new(null, "Dynamic group rebuild failed. Check the server log and try again.");
            }
            finally { db.ChangeTracker.Clear(); }
            await transaction.ReleaseSavepointAsync("dynamic_group", ct);
        }
        await transaction.CommitAsync(ct);
        return outcomes;
    }

    public async Task<DynamicGroupRebuildOutcomeDto> RunAsync(int groupId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateContextAsync();
            db.Database.SetCommandTimeout(30);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await DeviceGroupDependencies.LockAsync(db, ct);
            var groups = await db.DeviceGroups.ToDictionaryAsync(x => x.Id, ct);
            if (!groups.TryGetValue(groupId, out var group))
                return new(null, "Device group not found.");
            if (group.Type != DeviceGroupType.Dynamic || group.QueryDefinitionJson is null)
                return new(null, "The group does not have a dynamic query definition.");

            List<DeviceGroup> ordered;
            try { ordered = DeviceGroupDependencies.Ordered(groups, groupId); }
            catch (InvalidOperationException exception) { return new(null, exception.Message); }

            // Other membership invalidators also take group locks in ID order.
            foreach (var id in ordered.Select(x => x.Id).Order())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + id}, 0))", ct);
            var count = 0;
            foreach (var current in ordered)
            {
                ct.ThrowIfCancellationRequested();
                var definition = DeviceQueryDefinitionDto.Parse(current.QueryDefinitionJson!);
                var (query, error) = await DeviceQueryBuilder.BuildAsync(db, definition, ct);
                if (error is not null) return new(null, $"{current.Name}: {error}");
                var members = await ReplaceMembershipAsync(db, current, query!, ct);
                // Persist each dependency before evaluating its parents, but commit the whole
                // chain together so a failure preserves every previous membership snapshot.
                await db.SaveChangesAsync(ct);
                if (current.Id == groupId) count = members;
            }
            await transaction.CommitAsync(ct);
            return new(new(groupId, count, group.MembershipEvaluatedAtUtc!.Value), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Dynamic group rebuild failed for {GroupId}", groupId);
            return new(null, "Dynamic group rebuild failed. Check the server log and try again.");
        }
    }

    private static async Task<int> ReplaceMembershipAsync(CloneDeployDbContext db, DeviceGroup group, IQueryable<Device> query, CancellationToken ct)
    {
        var ids = await query.Select(x => x.Id).ToListAsync(ct);
        await db.DeviceGroupMemberships.Where(x => x.DeviceGroupId == group.Id).ExecuteDeleteAsync(ct);
        db.DeviceGroupMemberships.AddRange(ids.Select(deviceId => new DeviceGroupMembership { DeviceGroupId = group.Id, DeviceId = deviceId }));
        group.MembershipEvaluatedAtUtc = DateTime.UtcNow;
        return ids.Count;
    }
}
