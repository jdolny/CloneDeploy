using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

public sealed record DeviceRelationLink(string Name, string Href);

public sealed record DeviceRelation(string Kind, string Name, string Details, string? Href,
    IReadOnlyList<DeviceRelationLink> RelatedThrough)
{
    public bool Matches(string? search) => string.IsNullOrWhiteSpace(search) ||
        $"{Kind} {Name} {Details} {string.Join(' ', RelatedThrough.Select(x => x.Name))}"
            .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    public static List<DeviceRelation> Create(Device device, IEnumerable<CompiledPolicySnapshotDto> snapshots)
    {
        var rows = new List<DeviceRelation>();
        foreach (var group in device.Groups.Select(x => x.DeviceGroup).OfType<DeviceGroup>()
                     .Where(x => !x.IsHidden).DistinctBy(x => x.Id).OrderBy(x => x.Name))
            rows.Add(new("Groups", group.Name, UiText.Get(LocKeys.Ui.DeviceRelation.Text0GroupMembership, (UiText.Label(group.Type))), $"/groups/manage/{group.Id}", []));
        foreach (var tag in device.Tags.Select(x => x.Tag).OfType<Tag>().DistinctBy(x => x.Id).OrderBy(x => x.Name))
            rows.Add(new("Tags", tag.Name, UiText.Get(LocKeys.Ui.DeviceRelation.AssignedToThisDevice), "/shared/tags", []));
        if (device.DirectoryObject is { } directory)
            rows.Add(new("Directory", directory.DirectorySource.Name,
                directory.DistinguishedName ?? directory.AccountName ?? UiText.Get(LocKeys.User.DirectoryAccount), "/administration/server/directories", []));
        if (device.Image is { } image)
            rows.Add(new("Imaging", image.Name, UiText.Get(LocKeys.Ui.DeviceRelation.DeviceSDefaultImage), $"/images/manage/{image.Id}", []));
        if (device.DeploymentProfile is { } profile)
            rows.Add(new("Imaging", profile.Name, UiText.Get(LocKeys.Ui.DeviceRelation.DeviceSDefaultDeploymentProfile), $"/images/deploymentprofiles/{profile.Id}", []));

        var actions = new Dictionary<Guid, (CompiledPolicyActionDto Action, List<DeviceRelationLink> Policies, HashSet<string> Roles)>();
        foreach (var snapshot in snapshots.DistinctBy(x => x.Policy.Id).OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Name))
        {
            var policy = snapshot.Policy;
            rows.Add(new("Policies", policy.Name, UiText.Get(LocKeys.Ui.DeviceRelation.CurrentlyApplicablePriority01Actions, (policy.ExecutionPriority), (policy.Actions.Count)),
                $"/policies/{policy.Id}", []));
            var link = new DeviceRelationLink(policy.Name, $"/policies/{policy.Id}/actions");
            void AddAction(CompiledPolicyActionDto action, string role)
            {
                if (!actions.TryGetValue(action.Guid, out var entry))
                    entry = (action, [], []);
                if (!entry.Policies.Contains(link)) entry.Policies.Add(link);
                entry.Roles.Add(role);
                actions[action.Guid] = entry;
                if (action.Condition is { } condition) AddAction(condition, UiText.Get(LocKeys.Ui.ActionRelations.ActionCondition));
            }
            if (policy.Condition is { } condition) AddAction(condition, UiText.Get(LocKeys.Ui.ActionRelations.PolicyCondition));
            foreach (var action in policy.Actions) AddAction(action, UiText.Get(LocKeys.Ui.DeviceGroupRelations.PolicyAction));
        }
        foreach (var entry in actions.Values.OrderBy(x => x.Action.Name))
            rows.Add(new("Actions", entry.Action.Name, $"{entry.Action.Type} · {string.Join(", ", entry.Roles.Order())}",
                null, entry.Policies));
        return rows;
    }
}
