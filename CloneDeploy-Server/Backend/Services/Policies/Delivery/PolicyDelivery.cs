using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Determines which policies apply to a device through its groups, directory location, and imaging setup.
/// </summary>
internal static class PolicyDelivery
{
    internal static async Task<List<CompiledPolicySnapshotDto>> ReadAsync(CloneDeployDbContext db, int deviceId, CancellationToken ct = default)
        => (await ReadDetailsAsync(db, deviceId, ct)).Select(x => x.Snapshot).ToList();

    internal static async Task<List<EffectivePolicyDetailsDto>> ReadDetailsAsync(CloneDeployDbContext db, int deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct);
        if (device is null || device.ArchivedAtUtc != null) return [];
        // Setup is a server-ordered queue belonging to one completed imaging deployment.
        // While it is active, regular policy assignments wait. A failed step requires review.
        var deployment = await db.Set<ImagingDeployment>().AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId &&
                                                                                                    x.ImagingCompletedAtUtc != null && x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null, ct);
        if (deployment is not null)
        {
            if (deployment.WindowsSetupReadyAtUtc is null) return [];
            var step = await db.Set<ImagingDeploymentPolicy>().AsNoTracking().Where(x => x.ImagingDeploymentId == deployment.Id &&
                                                                                         (x.FinishedAtUtc == null || x.Result == 1)).OrderBy(x => x.Position).FirstOrDefaultAsync(ct);
            if (step is null || step.Result == 1) return [];
            return
            [
                new EffectivePolicyDetailsDto(JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(step.SnapshotJson)
                ?? throw new PolicyCompilationException("The post-deployment policy snapshot is invalid."),
                    [new("Imaging setup", "Post-deployment policy")])
            ];
        }

        var sources = new Dictionary<int, List<EffectivePolicySourceDto>>();
        void AddSource(int policyId, string assignment, string location)
        {
            if (!sources.TryGetValue(policyId, out var list)) sources[policyId] = list = [];
            list.Add(new(assignment, location));
        }
        var groups = DeviceGroupMembershipQuery.ForDevice(db, deviceId).Where(g => !g.IsHidden);
        var groupLinks = await (from link in db.GroupPolicies
            join deviceGroup in groups on link.DeviceGroupId equals deviceGroup.Id
            orderby deviceGroup.Name, deviceGroup.Id
            select new { link.PolicyId, deviceGroup.Name }).ToListAsync(ct);
        foreach (var link in groupLinks) AddSource(link.PolicyId, "Inherited", $"Group: {link.Name}");
        foreach (var id in await db.DevicePolicies.Where(x => x.DeviceId == deviceId).Select(x => x.PolicyId).ToListAsync(ct))
            AddSource(id, "Direct", "This device");
        if (device.DirectorySourceId is int sourceId && device.DirectoryObjectGuid is Guid objectGuid &&
            await db.DirectorySources.AnyAsync(x => x.Id == sourceId && x.IsEnabled && !x.ApplicationAccessOnly && x.Provider == DirectoryProvider.ActiveDirectory, ct))
        {
            var computer = await db.DirectoryObjects.AsNoTracking().SingleOrDefaultAsync(x => x.DirectorySourceId == sourceId &&
                                                                                              x.ObjectGuid == objectGuid && !x.IsMissing && x.Kind == DirectoryObjectKind.Computer, ct);
            if (computer?.ParentGuid is Guid directParent)
            {
                var parents = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == sourceId && !x.IsMissing &&
                                                                                  (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Container || x.Kind == DirectoryObjectKind.Domain))
                    .ToDictionaryAsync(x => x.ObjectGuid, ct);
                var ancestors = new HashSet<Guid>();
                Guid? current = directParent;
                while (current.HasValue && parents.TryGetValue(current.Value, out var node) && ancestors.Add(current.Value)) current = node.ParentGuid;
                var matching = await db.DirectoryOuPolicies.Where(x => x.DirectorySourceId == sourceId &&
                                                                       ancestors.Contains(x.OuGuid) && (x.OuGuid == directParent || x.IncludeDescendants)).ToListAsync(ct);
                var sourceName = await db.DirectorySources.Where(x => x.Id == sourceId).Select(x => x.Name).SingleAsync(ct);
                foreach (var link in matching.OrderBy(x => parents[x.OuGuid].DistinguishedName))
                {
                    var node = parents[link.OuGuid];
                    AddSource(link.PolicyId, "Inherited", $"OU: {sourceName} / {node.DistinguishedName ?? node.Name}");
                }
            }
        }

        var ids = sources.Keys.ToArray();
        var snapshots = await db.ActiveCompiledPolicies.AsNoTracking().Where(x => ids.Contains(x.PolicyId) &&
                                                                                  !x.Policy!.Archived && x.Policy.ArchivedAtUtc == null).Select(x => x.Json).ToListAsync(ct);
        var policies = snapshots.Select(x => JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(x)
                                             ?? throw new PolicyCompilationException("An active policy snapshot is invalid. Deactivate and activate that policy again.")).ToList();
        if (policies.Any(x => x.SchemaVersion is not (1 or 2 or 3))) throw new PolicyCompilationException("An active policy uses an unsupported snapshot format.");
        // These are applicable policies, not a run queue: triggers, schedules and client run history still govern execution.
        return policies.DistinctBy(x => x.Policy.Id)
            .OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Id)
            .Select(x => new EffectivePolicyDetailsDto(x, sources[x.Policy.Id].OrderBy(s => s.Assignment).ThenBy(s => s.Location).ToList())).ToList();
    }
}
