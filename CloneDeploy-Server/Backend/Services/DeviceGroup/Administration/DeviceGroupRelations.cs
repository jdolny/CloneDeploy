using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class DeviceGroupRelations
{
    internal static async Task<List<DeviceGroupRelationDto>> ReadAsync(CloneDeployDbContext db, int groupId)
    {
        var group = await db.DeviceGroups.AsNoTracking().Include(x => x.Image).SingleAsync(x => x.Id == groupId);
        var rows = new List<DeviceGroupRelationDto>();
        var memberIds = DeviceGroupMembershipQuery.DeviceIds(db, [groupId]);
        var members = await db.Devices.AsNoTracking().Where(x => memberIds.Contains(x.Id) && x.ArchivedAtUtc == null)
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync();
        foreach (var device in members)
            rows.Add(new("Devices", device.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.Text0GroupMember, (UiText.Label(group.Type))), $"/devices/manage/{device.Id}", []));
        var tags = await db.DeviceGroupTags.Where(x => x.DeviceGroupId == groupId).Select(x => x.Tag!).OrderBy(x => x.Name).ToListAsync();
        foreach (var tag in tags.DistinctBy(x => x.Id))
            rows.Add(new("Tags", tag.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.AssignedToThisGroup), "/shared/tags", []));
        if (group.Image is { } image)
            rows.Add(new("Imaging", image.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.GroupDefaultImage) + (image.Enabled ? "" : UiText.Get(LocKeys.Ui.DeviceGroupRelations.Disabled)), $"/images/manage/{image.Id}", []));
        var profile = await db.ImageProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == group.DeploymentProfileId);
        if (profile is not null)
            rows.Add(new("Imaging", profile.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.GroupDefaultDeploymentProfile) + (profile.Enabled ? "" : UiText.Get(LocKeys.Ui.DeviceGroupRelations.Disabled)), $"/images/deploymentprofiles/{profile.Id}", []));
        var policies = await db.GroupPolicies.Where(x => x.DeviceGroupId == groupId).Select(x => x.Policy!)
            .AsNoTracking().OrderBy(x => x.ExecutionPriority).ThenBy(x => x.Name).ToListAsync();
        var ids = policies.Select(x => x.Id).ToArray();
        var snapshots = await db.Set<CloneDeploy_Server.Backend.Data.Models.ActiveCompiledPolicy>().AsNoTracking()
            .Where(x => ids.Contains(x.PolicyId)).ToDictionaryAsync(x => x.PolicyId, x => x.Json);
        var actions = new Dictionary<Guid, (CompiledPolicyActionDto Action, List<DeviceGroupRelationLinkDto> Policies, HashSet<string> Roles)>();
        foreach (var policy in policies.DistinctBy(x => x.Id))
        {
            var published = snapshots.TryGetValue(policy.Id, out var json);
            var state = policy.Archived || policy.ArchivedAtUtc.HasValue ? UiText.Get(LocKeys.Device.ArchivedText) : published ? UiText.Get(LocKeys.Ui.DeviceGroupRelations.Published) : UiText.Get(LocKeys.Ui.DeviceGroupRelations.NotPublished);
            rows.Add(new("Policies", policy.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.AssignedToThisGroup0Priority1, (state), (policy.ExecutionPriority)), $"/policies/{policy.Id}", []));
            if (!published) continue;
            var snapshot = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(json!)
                ?? throw new InvalidDataException("Invalid published policy snapshot.");
            var link = new DeviceGroupRelationLinkDto(policy.Name, $"/policies/{policy.Id}/actions");
            void Add(CompiledPolicyActionDto action, string role)
            {
                if (!actions.TryGetValue(action.Guid, out var entry)) entry = (action, [], []);
                if (!entry.Policies.Contains(link)) entry.Policies.Add(link);
                entry.Roles.Add(role);
                actions[action.Guid] = entry;
                if (action.Condition is { } condition) Add(condition, UiText.Get(LocKeys.Ui.ActionRelations.ActionCondition));
            }
            if (snapshot.Policy.Condition is { } condition) Add(condition, UiText.Get(LocKeys.Ui.ActionRelations.PolicyCondition));
            foreach (var action in snapshot.Policy.Actions) Add(action, UiText.Get(LocKeys.Ui.DeviceGroupRelations.PolicyAction));
        }
        foreach (var entry in actions.Values.OrderBy(x => x.Action.Name))
            rows.Add(new("Actions", entry.Action.Name, UiText.Get(LocKeys.Ui.DeviceGroupRelations.PublishedPolicyReference01, (UiText.Label(entry.Action.Type)), (string.Join(", ", entry.Roles.Order()))), null, entry.Policies));
        return rows;
    }
}
