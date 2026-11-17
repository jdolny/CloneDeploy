using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

internal static class DeviceGroupDependencies
{
    // Always take this before individual group locks. ReadCommitted ensures a waiter
    // validates the graph committed by the previous lock holder.
    internal static Task LockAsync(CloneDeployDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group-dependencies"}, 0))", ct);

    internal static int[] References(DeviceQueryDefinitionDto definition)
    {
        var ids = new HashSet<int>();
        var count = 0;
        void Visit(DeviceQueryNodeDto? node, int depth)
        {
            if (node is null || node.Children is null || depth > 6 || ++count > 100)
                throw new InvalidOperationException("Use at most 100 conditions and six nesting levels.");
            if (node.Scope == "DeviceGroup")
            {
                if (!int.TryParse(node.Value, out var id) || id <= 0)
                    throw new InvalidOperationException("Select an available device group.");
                ids.Add(id);
            }
            foreach (var child in node.Children) Visit(child, depth + 1);
        }
        Visit(definition.Root, 0);
        return ids.ToArray();
    }

    internal static List<DeviceGroup> Ordered(IReadOnlyDictionary<int, DeviceGroup> groups, int root)
    {
        var done = new HashSet<int>();
        var path = new List<int>();
        var ordered = new List<DeviceGroup>();
        void Visit(int id)
        {
            if (!groups.TryGetValue(id, out var group))
                throw new InvalidOperationException($"Device group {id} no longer exists. Select a replacement.");
            if (path.Contains(id))
                throw new InvalidOperationException("Device group cycle: " + string.Join(" → ", path.Append(id).Select(x => groups[x].Name)));
            if (done.Contains(id)) return;
            if (path.Count >= 128)
                throw new InvalidOperationException("Device group references exceed the maximum depth of 128.");
            if (group.Type == DeviceGroupType.Dynamic)
            {
                if (group.QueryDefinitionJson is null)
                    throw new InvalidOperationException($"Save dynamic criteria for '{group.Name}' before referencing it.");
                path.Add(id);
                foreach (var dependency in References(DeviceQueryDefinitionDto.Parse(group.QueryDefinitionJson))) Visit(dependency);
                path.RemoveAt(path.Count - 1);
                ordered.Add(group);
            }
            done.Add(id);
        }
        Visit(root);
        return ordered;
    }

    internal static async Task<string?> ValidateAsync(CloneDeployDbContext db, DeviceGroup proposed, CancellationToken ct = default)
    {
        try
        {
            var groups = await db.DeviceGroups.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            groups[proposed.Id] = proposed;
            Ordered(groups, proposed.Id);
            return null;
        }
        catch (InvalidOperationException exception) { return exception.Message; }
    }
}
