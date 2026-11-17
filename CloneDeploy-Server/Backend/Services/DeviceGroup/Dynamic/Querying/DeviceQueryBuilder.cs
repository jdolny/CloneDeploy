using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Checks device selection rules and prepares them for finding matching devices.
/// </summary>
internal static class DeviceQueryBuilder
{
    internal static async Task<(IQueryable<Device>? Query, string? Error)> BuildAsync(CloneDeployDbContext db, DeviceQueryDefinitionDto definition, CancellationToken ct)
    {
        if (definition.Root is null)
            return (null, "The query root is missing.");

        // Load the small hierarchy only when the query contains a directory condition.
        bool HasDirectory(DeviceQueryNodeDto node, int depth = 0) => depth > 6 || node.Scope is "DirectoryGroup" or "DirectoryOu" ||
                                                                     (node.Children?.Any(x => x != null && HasDirectory(x, depth + 1)) ?? false);

        var directory = HasDirectory(definition.Root)
            ? await db.DirectoryObjects.AsNoTracking()
                .Where(x => !x.IsMissing && x.Kind != DirectoryObjectKind.User && x.Kind != DirectoryObjectKind.Computer &&
                            db.DirectorySources.Any(s => s.Id == x.DirectorySourceId && s.IsEnabled && !s.ApplicationAccessOnly)).ToListAsync(ct)
            : [];
        System.Linq.Expressions.Expression<Func<Device, bool>> predicate;
        try
        {
            predicate = DeviceQueryCompiler.Compile(definition, db, directory);
        }
        catch (InvalidOperationException exception)
        {
            return (null, exception.Message);
        }

        var nodes = new List<DeviceQueryNodeDto>();

        void Collect(DeviceQueryNodeDto node)
        {
            nodes.Add(node);
            foreach (var child in node.Children)
                Collect(child);
        }

        Collect(definition.Root); // Compiler has already enforced depth, size and field/value validity.
        var groupIds = DeviceGroupDependencies.References(definition);
        if (groupIds.Length > 0 && await db.DeviceGroups.CountAsync(x => groupIds.Contains(x.Id), ct) != groupIds.Length)
            return (null, "A referenced device group no longer exists. Select a replacement before evaluating.");
        var attributes = nodes.Where(x => x.Field == "CustomAttributeId").Select(x => int.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
        var scripts = nodes.Where(x => x.Field == "ScriptActionId").Select(x => int.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
        if (attributes.Length > 0 && await db.CustomAttributes.CountAsync(x => attributes.Contains(x.Id), ct) != attributes.Length ||
            scripts.Length > 0 && await db.ScriptActions.CountAsync(x => scripts.Contains(x.Id), ct) != scripts.Length)
            return (null, "A custom attribute or inventory script no longer exists. Select a replacement before evaluating.");
        return (db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != Backend.Enum.DeviceProvisionStatus.Archived).Where(predicate), null);
    }
}