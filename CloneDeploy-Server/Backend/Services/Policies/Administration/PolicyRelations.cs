using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class PolicyRelations
{
    internal static async Task<List<PolicyRelationDto>> ReadAsync(CloneDeployDbContext db, PolicyDetailsDto details, bool canViewDevices)
    {
        var policy = details.Policy;
        var rows = new List<PolicyRelationDto>();
        foreach (var assignment in policy.PolicyActions.OrderBy(x => x.Order))
        {
            var action = details.Actions.FirstOrDefault(x => x.Guid == assignment.ActionGuid);
            rows.Add(new("Actions", action?.Name ?? UiText.Get(LocKeys.Ui.PolicyReview.UnavailableAction), UiText.Get(LocKeys.Ui.PolicyRelations.AssignedActionOrder01, (assignment.Order), (UiText.Label(assignment.ActionType))), action?.Url));
        }
        var conditionIds = policy.PolicyActions.Select(x => x.ConditionId).Append(policy.ConditionId).OfType<int>().Distinct().ToArray();
        var conditions = await db.ScriptActions.AsNoTracking().Where(x => conditionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        foreach (var id in conditionIds)
        {
            var condition = conditions.GetValueOrDefault(id);
            var roles = new List<string>();
            if (policy.ConditionId == id) roles.Add(UiText.Get(LocKeys.Ui.ActionRelations.PolicyCondition));
            foreach (var assignment in policy.PolicyActions.Where(x => x.ConditionId == id))
                roles.Add(UiText.Get(LocKeys.Ui.PolicyRelations.ConditionFor0, (details.Actions.FirstOrDefault(x => x.Guid == assignment.ActionGuid)?.Name ?? UiText.Get(LocKeys.Ui.PolicyRelations.UnavailableAction))));
            rows.Add(new("Conditions", condition?.Name ?? UiText.Get(LocKeys.Ui.PolicyActionsTab.UnavailableCondition), string.Join(" · ", roles), condition is null ? null : $"/actions/script/{id}"));
        }
        if (canViewDevices)
        {
            var devices = await db.DevicePolicies.Where(x => x.PolicyId == policy.Id).Select(x => x.Device).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            foreach (var device in devices)
                rows.Add(new("Devices", device.Name, UiText.Get(LocKeys.Ui.PolicyRelations.DirectDeviceTarget) + (device.ArchivedAtUtc.HasValue ? UiText.Get(LocKeys.Ui.PolicyRelations.Archived) : ""), $"/devices/manage/{device.Id}"));
        }
        var groups = await db.GroupPolicies.Where(x => x.PolicyId == policy.Id).Select(x => x.DeviceGroup!).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        foreach (var group in groups)
            rows.Add(new("Groups", group.Name, UiText.Get(LocKeys.Ui.PolicyRelations.Text0GroupTarget, (UiText.Label(group.Type))), $"/groups/manage/{group.Id}"));
        var ous = await db.DirectoryOuPolicies.AsNoTracking().Where(x => x.PolicyId == policy.Id).ToListAsync();
        var sourceIds = ous.Select(x => x.DirectorySourceId).Distinct().ToArray();
        var nodes = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => sourceIds.Contains(x.DirectorySourceId) && (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Domain)).ToListAsync();
        foreach (var target in ous)
        {
            var node = nodes.FirstOrDefault(x => x.DirectorySourceId == target.DirectorySourceId && x.ObjectGuid == target.OuGuid);
            var available = node is { IsMissing: false } && node.DirectorySource.IsEnabled
                && !node.DirectorySource.ApplicationAccessOnly && node.DirectorySource.Provider == DirectoryProvider.ActiveDirectory;
            rows.Add(new("Directory", node?.Name ?? UiText.Get(LocKeys.Ui.PolicyRelations.UnavailableOU),
                $"{(target.IncludeDescendants ? "OU and descendants" : "Direct OU")} · {node?.DistinguishedName ?? target.OuGuid.ToString()}" + (available ? "" : UiText.Get(LocKeys.Ui.PolicyRelations.Unavailable)),
                node is null ? null : $"/administration/server/directories/{target.DirectorySourceId}"));
        }
        var tags = await db.PolicyTags.Where(x => x.PolicyId == policy.Id).Select(x => x.Tag!).AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        foreach (var tag in tags)
            rows.Add(new("Tags", tag.Name, UiText.Get(LocKeys.Ui.PolicyRelations.AssignedToThisPolicy), "/shared/tags"));
        var access = await db.Set<PolicyUserAccess>().AsNoTracking().SingleOrDefaultAsync(x => x.PolicyId == policy.Id);
        if (access is not null)
        {
            var ids = new[] { access.LoginAudienceId, access.SelfServiceAudienceId }.OfType<int>().Distinct().ToArray();
            var audiences = await db.Set<UserAudience>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
            foreach (var id in ids)
            {
                var roles = new List<string>();
                if (access.LoginAudienceId == id)
                    roles.Add(policy.Trigger == PolicyTrigger.Login ? UiText.Get(LocKeys.Ui.PolicyRelations.LoginAudience) : UiText.Get(LocKeys.Ui.PolicyRelations.NotificationRecipientAudience));
                if (access.SelfServiceAudienceId == id)
                    roles.Add(UiText.Get(LocKeys.Ui.PolicyAudiencesTab.SelfServiceAudience) + (!access.SelfServiceEnabled ? UiText.Get(LocKeys.Ui.PolicyRelations.SelfServiceDisabled) : access.AnyInteractiveUser ? UiText.Get(LocKeys.Ui.PolicyRelations.AnyInteractiveUserSelectedInstead) :
                        access.RestrictSelfServiceToDeviceTargets ? UiText.Get(LocKeys.Ui.PolicyRelations.RestrictedToDeviceTargets) : UiText.Get(LocKeys.Ui.PolicyRelations.AcrossEnrolledDevices)));
                rows.Add(new("Audiences", audiences.GetValueOrDefault(id)?.Name ?? UiText.Get(LocKeys.Ui.PolicyReview.UnavailableAudience), string.Join(" · ", roles),
                    audiences.ContainsKey(id) ? $"/shared/user-audiences/{id}" : null));
            }
        }
        return rows.OrderBy(x => x.Kind).ThenBy(x => x.Name).ToList();
    }
}
