using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds the devices included in a deployment's selected groups and directory locations.
/// </summary>
internal static class DeploymentTargets
{
    internal static async Task<List<PolicyTargetOptionDto>> ReadOptionsAsync(CloneDeployDbContext db)
    {
        var groups = await db.DeviceGroups.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var counts = await (from membership in db.DeviceGroupMemberships
            join device in db.Devices on membership.DeviceId equals device.Id
            where device.ArchivedAtUtc == null
            group membership by membership.DeviceGroupId
            into members
            select new { Id = members.Key, Count = members.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count);
        var allDevicesCount = await DeviceGroupMembershipQuery.AllDevices(db).CountAsync();
        var options = groups.Select(x => new PolicyTargetOptionDto(PolicyTargetKeyDto.Group(x.Id), x.Name,
                x.Type == DeviceGroupType.AllDevices ? "Built-in group · All enrolled, non-archived devices" : x.Type == DeviceGroupType.Dynamic ? $"Dynamic group · Last evaluated: {(x.MembershipEvaluatedAtUtc.HasValue ? x.MembershipEvaluatedAtUtc.Value.ToString("u") : "not yet evaluated")}" : "Manual group", !x.IsHidden)
            { GroupType = x.Type == DeviceGroupType.AllDevices ? "Built-in" : x.Type.ToString(), GroupDescription = x.Description ?? "", DeviceCount = x.Type == DeviceGroupType.AllDevices ? allDevicesCount : counts.GetValueOrDefault(x.Id) }).ToList();
        var ous = await (from ou in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on ou.DirectorySourceId equals source.Id
            where ou.Kind == DirectoryObjectKind.OrganizationalUnit && source.Provider == DirectoryProvider.ActiveDirectory
            orderby source.Name, ou.DistinguishedName
            select new { ou.DirectorySourceId, ou.ObjectGuid, ou.Name, ou.DistinguishedName, ou.IsMissing, SourceName = source.Name, IsEnabled = source.IsEnabled && !source.ApplicationAccessOnly }).ToListAsync();
        options.AddRange(ous.Select(x => new PolicyTargetOptionDto(PolicyTargetKeyDto.Ou(x.DirectorySourceId, x.ObjectGuid), x.Name,
            $"{x.SourceName} · {x.DistinguishedName}", !x.IsMissing && x.IsEnabled)));
        return options;
    }

    private static Guid[] ExpandParents(List<DirectoryObject> directory, IEnumerable<PolicyTargetSelectionDto> selections)
    {
        var children = directory.Where(x => x.ParentGuid.HasValue).ToLookup(x => x.ParentGuid!.Value);
        var included = new HashSet<Guid>();
        foreach (var selection in selections)
        {
            var root = selection.Key.OuGuid!.Value;
            included.Add(root);
            if (!selection.IncludeDescendants) continue;
            var visited = new HashSet<Guid> { root };
            var queue = new Queue<Guid>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var parent))
                foreach (var child in children[parent])
                    if (visited.Add(child.ObjectGuid))
                    {
                        included.Add(child.ObjectGuid);
                        queue.Enqueue(child.ObjectGuid);
                    }
        }

        return included.ToArray();
    }

    internal static async Task<DeploymentCatalogDto> ReadCatalogAsync(CloneDeployDbContext db)
    {
        var options = await ReadOptionsAsync(db);
        var nodes = await (from node in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on node.DirectorySourceId equals source.Id
            where source.Provider == DirectoryProvider.ActiveDirectory && !node.IsMissing &&
                  (node.Kind == DirectoryObjectKind.Domain || node.Kind == DirectoryObjectKind.Container || node.Kind == DirectoryObjectKind.OrganizationalUnit)
            orderby source.Name, node.Name
            select new PolicyTargetDirectoryNodeDto(source.Id, source.Name, node.ObjectGuid, node.ParentGuid,
                node.Name, node.DistinguishedName, node.Kind == DirectoryObjectKind.OrganizationalUnit, (source.IsEnabled && !source.ApplicationAccessOnly))).ToListAsync();
        return new(options, nodes);
    }

    internal static async Task<(List<Device> Devices, string? Error)> ResolveAsync(CloneDeployDbContext db, DeploymentRequestDto request)
    {
        if (request.Targets.Count + request.DeviceIds.Count == 0)
            return ([], "Choose at least one target.");
        var options = await ReadOptionsAsync(db);
        if (request.Targets.Any(x => !x.Key.IsValid || !options.Any(o => o.Key == x.Key && o.Available)))
            return ([], "A selected group or OU is unavailable. Refresh your targets.");
        var explicitIds = request.DeviceIds.Distinct().ToArray();
        if (await db.Devices.CountAsync(x => explicitIds.Contains(x.Id) && x.ArchivedAtUtc == null) != explicitIds.Length)
            return ([], "A selected device is unavailable.");
        var groups = request.Targets.Where(x => x.Key.GroupId.HasValue).Select(x => x.Key.GroupId!.Value).ToArray();
        IQueryable<int> ids = DeviceGroupMembershipQuery.DeviceIds(db, groups);
        foreach (var source in request.Targets.Where(x => x.Key.IsOu).GroupBy(x => x.Key.DirectorySourceId!.Value))
        {
            var sourceId = source.Key;
            var directory = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == sourceId && !x.IsMissing &&
                                                                                (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Container)).ToListAsync();
            var parents = ExpandParents(directory, source);
            ids = ids.Union(db.Devices.Where(d => d.DirectorySourceId == sourceId &&
                                                  db.DirectoryObjects.Any(o => o.DirectorySourceId == sourceId && !o.IsMissing && o.Kind == DirectoryObjectKind.Computer &&
                                                                               o.ObjectGuid == d.DirectoryObjectGuid && o.ParentGuid.HasValue && parents.Contains(o.ParentGuid.Value))).Select(d => d.Id));
        }

        var devices = await db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && (ids.Contains(x.Id) || explicitIds.Contains(x.Id)))
            .OrderBy(x => x.Id).ToListAsync();
        if (devices.Any(x => x.Guid == Guid.Empty))
            return ([], "A matching device has no valid agent identity.");
        return (devices, null);
    }
}