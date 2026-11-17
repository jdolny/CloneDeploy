using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages policy assignments to devices, groups and directory locations and previews affected devices.
/// </summary>
public sealed class PolicyTargetService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<PolicyTargetStateDto>> GetPolicyTargetsAsync(int policyId)
    {
        if (await GetViewUserAsync() is null)
            return ServiceResult<PolicyTargetStateDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var state = await ReadPolicyStateAsync(db, policyId);
        if (state is null)
            return ServiceResult<PolicyTargetStateDto>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.PolicyOrTargetNotFound));
        state.Options = await ReadOptionsAsync(db);
        state.NotificationAudienceAllowed = await NotificationPolicyAudience.EligibleAsync(db, policyId);
        var access = await db.Set<PolicyUserAccess>().AsNoTracking().SingleOrDefaultAsync(x => x.PolicyId == policyId);
        state.LoginAudienceId = access?.LoginAudienceId;
        state.AudienceRevision = access?.Revision ?? Guid.Empty;
        state.Audiences = await db.Set<UserAudience>().AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        state.Options.AddRange(await ReadDeviceOptionsAsync(db, state.Selected.Select(x => x.Key.DeviceId).OfType<int>().ToArray()));
        state.CanSelectOus = await db.DirectorySources.AnyAsync(source =>
            source.Provider == DirectoryProvider.ActiveDirectory && source.IsEnabled && !source.ApplicationAccessOnly);
        state.DirectoryNodes = await (from node in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on node.DirectorySourceId equals source.Id
            where source.Provider == DirectoryProvider.ActiveDirectory && source.IsEnabled && !source.ApplicationAccessOnly && !node.IsMissing &&
                  (node.Kind == DirectoryObjectKind.Domain || node.Kind == DirectoryObjectKind.Container || node.Kind == DirectoryObjectKind.OrganizationalUnit)
            orderby source.Name, node.Name
            select new PolicyTargetDirectoryNodeDto(source.Id, source.Name, node.ObjectGuid, node.ParentGuid,
                node.Name, node.DistinguishedName, node.Kind == DirectoryObjectKind.OrganizationalUnit || node.Kind == DirectoryObjectKind.Domain, (source.IsEnabled && !source.ApplicationAccessOnly))).ToListAsync();
        state.Conditions = await db.ScriptActions.Where(x => x.IsCondition && x.ActionReference!.ArchivedAtUtc == null)
            .OrderBy(x => x.Name).Select(x => new PolicyConditionOptionDto(x.Id, x.Name)).ToListAsync();
        return ServiceResult<PolicyTargetStateDto>.Ok(state);
    }

    public async Task<ServiceResult> SavePolicyTargetsAsync(PolicyTargetStateDto state)
    {
        return await SavePolicyTargetsAsync(state, true);
    }

    public async Task<ServiceResult> SavePolicyTargetsAsync(PolicyTargetStateDto state, bool saveCondition)
    {
        var user = await GetEditUserAsync();
        if (user is null)
            return ServiceResult.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (state.SaveLoginAudience)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947234, {state.PolicyId})");
            await UserAudienceService.LockAsync(db);
        }
        var current = await ReadPolicyStateAsync(db, state.PolicyId);
        if (current is null)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.PolicyOrTargetNotFound));
        if (!saveCondition)
        {
            if (current.AssignmentsVersion != state.AssignmentsVersion)
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.TargetsChangedElsewhereReloadTargetsBeforeSaving));
            state.ConditionId = current.ConditionId;
            state.ConditionFailedAction = current.ConditionFailedAction;
        }

        if (saveCondition && current.Active && (current.ConditionId != state.ConditionId || current.ConditionFailedAction != state.ConditionFailedAction))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.DeactivateThePolicyBeforeChangingItsRunCondition));
        if (current.Archived) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.RestoreThePolicyBeforeChangingItsTargets));
        if (saveCondition && current.Version != state.Version) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.TargetsOrConditionsChangedElsewhereReloadTargetingBeforeSaving));
        var selectionError = await ValidateSelectionsAsync(db, state.Selected, current.Selected);
        if (selectionError is not null)
            return ServiceResult.Fail("Policy.Validation", selectionError);
        if (saveCondition && (!System.Enum.IsDefined(state.ConditionFailedAction) || state.ConditionFailedAction == PolicyConditionFailedAction.GotoModule))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.ChooseAValidPolicyConditionResult));
        if (saveCondition && !current.Active && state.ConditionId.HasValue && !await db.ScriptActions.AnyAsync(x => x.Id == state.ConditionId && x.IsCondition && x.ActionReference!.ArchivedAtUtc == null))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.ChooseAnAvailableConditionScript));

        if (saveCondition && state.ConditionId.HasValue && current.Trigger != PolicyTrigger.Login &&
            await db.Set<PolicyUserAccess>().AnyAsync(x => x.PolicyId == state.PolicyId && x.LoginAudienceId != null))
            return ServiceResult.Fail("Policy.Validation", NotificationPolicyAudience.ValidationMessage);

        PolicyUserAccess? audienceAccess = null;
        int? previousAudienceId = null;
        if (state.SaveLoginAudience)
        {
            if (state.LoginAudienceId != null && current.Trigger != PolicyTrigger.Login && !await NotificationPolicyAudience.EligibleAsync(db, state.PolicyId))
                return ServiceResult.Fail("Policy.Validation", NotificationPolicyAudience.ValidationMessage);
            audienceAccess = await db.Set<PolicyUserAccess>().SingleOrDefaultAsync(x => x.PolicyId == state.PolicyId);
            if ((audienceAccess?.Revision ?? Guid.Empty) != state.AudienceRevision)
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.TheAudienceChangedElsewhereReopenTargetsBeforeSaving));
            if (state.LoginAudienceId is int audienceId && !await db.Set<UserAudience>().AnyAsync(x => x.Id == audienceId))
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.TheSelectedUserAudienceNoLongerExistsReopenTargets));
            previousAudienceId = audienceAccess?.LoginAudienceId;
            if (audienceAccess is null)
            {
                audienceAccess = new PolicyUserAccess { PolicyId = state.PolicyId };
                db.Add(audienceAccess);
            }
            audienceAccess.LoginAudienceId = state.LoginAudienceId;
            audienceAccess.Revision = Guid.NewGuid();
        }

        var deviceLinks = await db.DevicePolicies.Where(x => x.PolicyId == state.PolicyId).ToListAsync();
        db.DevicePolicies.RemoveRange(deviceLinks.Where(x => state.Selected.All(s => s.Key != PolicyTargetKeyDto.Device(x.DeviceId))));
        var groupLinks = await db.GroupPolicies.Where(x => x.PolicyId == state.PolicyId).ToListAsync();
        var ouLinks = await db.DirectoryOuPolicies.Where(x => x.PolicyId == state.PolicyId).ToListAsync();
        db.GroupPolicies.RemoveRange(groupLinks.Where(x => state.Selected.All(s => s.Key != PolicyTargetKeyDto.Group(x.DeviceGroupId))));
        db.DirectoryOuPolicies.RemoveRange(ouLinks.Where(x => state.Selected.All(s => s.Key != PolicyTargetKeyDto.Ou(x.DirectorySourceId, x.OuGuid))));
        foreach (var selection in state.Selected)
        {
            var key = selection.Key;
            if (key.DeviceId is { } deviceId)
            {
                if (deviceLinks.All(x => x.DeviceId != deviceId))
                    db.DevicePolicies.Add(new DevicePolicy { PolicyId = state.PolicyId, DeviceId = deviceId });
            }
            else if (key.GroupId is { } groupId)
            {
                // Assignments define scope; execution priority belongs to the policy.
                if (groupLinks.All(x => x.DeviceGroupId != groupId))
                {
                    db.GroupPolicies.Add(new GroupPolicy { PolicyId = state.PolicyId, DeviceGroupId = groupId });
                }
            }
            else
            {
                var link = ouLinks.SingleOrDefault(x => x.DirectorySourceId == key.DirectorySourceId && x.OuGuid == key.OuGuid);
                if (link is null)
                {
                    link = new DirectoryOuPolicy { PolicyId = state.PolicyId, DirectorySourceId = key.DirectorySourceId!.Value, OuGuid = key.OuGuid!.Value };
                    db.DirectoryOuPolicies.Add(link);
                }

                link.IncludeDescendants = selection.IncludeDescendants;
            }
        }

        var policy = await db.Policies.SingleAsync(x => x.Id == state.PolicyId);
        policy.ConditionId = state.ConditionId;
        policy.ConditionFailedAction = state.ConditionFailedAction;
        Audit(db, user, policy, new { Section = "Targeting", PreviousTargets = current.Selected.Select(x => new { x.Key, x.IncludeDescendants }), Targets = state.Selected.Select(x => new { x.Key, x.IncludeDescendants }), state.ConditionId, state.ConditionFailedAction });
        if (state.SaveLoginAudience)
            Audit(db, user, policy, new { Section = "User audience", PreviousAudienceId = previousAudienceId, state.LoginAudienceId });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        if (audienceAccess is not null) state.AudienceRevision = audienceAccess.Revision;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<PolicyTargetPreviewDto>> PreviewAsync(PolicyTargetStateDto state)
    {
        var user = await GetViewUserAsync();
        if (user is null)
            return ServiceResult<PolicyTargetPreviewDto>.Fail("Policy.Forbidden");
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded) return ServiceResult<PolicyTargetPreviewDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        if (!await db.Policies.AnyAsync(x => x.Id == state.PolicyId)) return ServiceResult<PolicyTargetPreviewDto>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyAudienceService.PolicyNotFound));
        var selectionError = await ValidateSelectionsAsync(db, state.Selected, []);
        if (selectionError is not null)
            return ServiceResult<PolicyTargetPreviewDto>.Fail("Policy.Validation", selectionError);
        var groupIds = state.Selected.Where(x => x.Key.GroupId.HasValue).Select(x => x.Key.GroupId!.Value).ToArray();
        var directIds = state.Selected.Where(x => x.Key.IsDevice).Select(x => x.Key.DeviceId!.Value).ToArray();
        IQueryable<int> deviceIds = DeviceGroupMembershipQuery.DeviceIds(db, groupIds)
            .Union(db.Devices.Where(x => directIds.Contains(x.Id)).Select(x => x.Id));
        foreach (var source in state.Selected.Where(x => x.Key.IsOu).GroupBy(x => x.Key.DirectorySourceId!.Value))
        {
            var sourceId = source.Key;
            var directory = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == sourceId && !x.IsMissing &&
                                                                                (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Container || x.Kind == DirectoryObjectKind.Domain)).ToListAsync();
            var parents = ExpandParents(directory, source);
            deviceIds = deviceIds.Union(db.Devices.Where(d => d.DirectorySourceId == sourceId &&
                                                              db.DirectoryObjects.Any(o => o.DirectorySourceId == sourceId && !o.IsMissing && o.Kind == DirectoryObjectKind.Computer &&
                                                                                           o.ObjectGuid == d.DirectoryObjectGuid && o.ParentGuid.HasValue && parents.Contains(o.ParentGuid.Value))).Select(d => d.Id));
        }

        // Select from Devices rather than returning membership rows, so overlapping targets count once.
        var devices = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && deviceIds.Contains(x.Id));
        var total = await devices.CountAsync();
        var sample = await devices.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100).Select(x => new PolicyTargetDeviceDto(x.Id, x.Name)).ToListAsync();
        return ServiceResult<PolicyTargetPreviewDto>.Ok(new(total, sample));
    }

    private async Task<ClaimsPrincipal?> GetGroupTargetUserAsync(PolicyTargetKeyDto key, string permission)
    {
        if (!key.IsValid || key.GroupId is null) return null;
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }

    public async Task<ServiceResult<TargetPolicyStateDto>> GetTargetPoliciesAsync(PolicyTargetKeyDto key)
    {
        if (await GetGroupTargetUserAsync(key, Constants.Permissions.DeviceGroupView) is null && await GetViewUserAsync() is null)
            return ServiceResult<TargetPolicyStateDto>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        if (!key.IsValid) return ServiceResult<TargetPolicyStateDto>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.SelectADeviceGroupOrOrganizationalUnit));
        var state = await ReadTargetStateAsync(db, key);
        if (state is null)
            return ServiceResult<TargetPolicyStateDto>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.PolicyOrTargetNotFound));
        state.AvailablePolicies = await db.Policies.AsNoTracking().Where(x => !x.Archived && x.ArchivedAtUtc == null)
            .OrderBy(x => x.Name).Select(x => new TargetPolicyOptionDto(x.Id, x.Name) { ExecutionPriority = x.ExecutionPriority }).ToListAsync();
        return ServiceResult<TargetPolicyStateDto>.Ok(state);
    }

    public async Task<ServiceResult> SaveTargetPoliciesAsync(TargetPolicyStateDto state)
    {
        var user = await GetGroupTargetUserAsync(state.Key, Constants.Permissions.DeviceGroupEdit) ?? await GetEditUserAsync();
        if (user is null)
            return ServiceResult.Fail("Policy.Forbidden");
        if (!state.Key.IsValid) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.SelectADeviceGroupOrOrganizationalUnit));
        if (state.Assignments.Select(x => x.PolicyId).Distinct().Count() != state.Assignments.Count) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.APolicyCanOnlyBeAssignedOnceToThis));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var current = await ReadTargetStateAsync(db, state.Key);
        if (current is null)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.PolicyOrTargetNotFound));
        if (current.Version != state.Version) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.AssignmentsChangedElsewhereReloadThePolicyListBeforeSaving));
        var options = await ReadOptionsAsync(db);
        if (state.Key.DeviceId is { } targetDeviceId)
            options.AddRange(await ReadDeviceOptionsAsync(db, [targetDeviceId]));
        if (!options.Any(x => x.Key == state.Key && x.Available) && state.Assignments.Count > 0)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.ThisTargetIsUnavailableRemoveItsAssignmentsOrSelect));
        var ids = state.Assignments.Select(x => x.PolicyId).ToArray();
        var policies = await db.Policies.Where(x => ids.Contains(x.Id)).ToListAsync();
        if (policies.Count != ids.Length || policies.Any(x => x.Archived || x.ArchivedAtUtc.HasValue)) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.PolicyTargetService.ChooseExistingNonArchivedPolicies));
        if (state.Key.DeviceId is { } deviceId)
        {
            var links = await db.DevicePolicies.Where(x => x.DeviceId == deviceId).ToListAsync();
            db.DevicePolicies.RemoveRange(links.Where(x => !ids.Contains(x.PolicyId)));
            foreach (var id in ids)
                if (links.All(x => x.PolicyId != id))
                    db.DevicePolicies.Add(new DevicePolicy { DeviceId = deviceId, PolicyId = id });
        }
        else if (state.Key.GroupId is { } groupId)
        {
            var links = await db.GroupPolicies.Where(x => x.DeviceGroupId == groupId).ToListAsync();
            db.GroupPolicies.RemoveRange(links.Where(x => !ids.Contains(x.PolicyId)));
            for (var index = 0; index < state.Assignments.Count; index++)
            {
                var item = state.Assignments[index];
                var link = links.SingleOrDefault(x => x.PolicyId == item.PolicyId);
                if (link is null)
                {
                    link = new GroupPolicy { DeviceGroupId = groupId, PolicyId = item.PolicyId };
                    db.GroupPolicies.Add(link);
                }
            }
        }
        else
        {
            var key = state.Key;
            var links = await db.DirectoryOuPolicies.Where(x => x.DirectorySourceId == key.DirectorySourceId && x.OuGuid == key.OuGuid).ToListAsync();
            db.DirectoryOuPolicies.RemoveRange(links.Where(x => !ids.Contains(x.PolicyId)));
            for (var index = 0; index < state.Assignments.Count; index++)
            {
                var item = state.Assignments[index];
                var link = links.SingleOrDefault(x => x.PolicyId == item.PolicyId);
                if (link is null)
                {
                    link = new DirectoryOuPolicy { DirectorySourceId = key.DirectorySourceId!.Value, OuGuid = key.OuGuid!.Value, PolicyId = item.PolicyId };
                    db.DirectoryOuPolicies.Add(link);
                }

                link.IncludeDescendants = item.IncludeDescendants;
            }
        }

        if (state.Key.GroupId is { } auditGroupId)
        {
            var auditGroup = await db.DeviceGroups.SingleAsync(x => x.Id == auditGroupId);
            DeviceGroupAudit.Add(db, user, auditGroupId, auditGroup.Name, AuditType.Update, "Policy assignments");
        }

        var affectedIds = current.Assignments.Select(x => x.PolicyId).Union(ids).ToArray();
        foreach (var policy in await db.Policies.Where(x => affectedIds.Contains(x.Id)).ToListAsync())
        {
            var assignment = state.Assignments.FirstOrDefault(x => x.PolicyId == policy.Id);
            Audit(db, user, policy, new
            {
                Section = "Targeting", Target = state.Key, Assigned = assignment is not null,
                assignment?.IncludeDescendants
            });
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static async Task<PolicyTargetStateDto?> ReadPolicyStateAsync(CloneDeployDbContext db, int policyId)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == policyId);
        if (policy is null) return null;
        var state = new PolicyTargetStateDto
        {
            PolicyId = policyId, Trigger = policy.Trigger, Archived = policy.Archived || policy.ArchivedAtUtc.HasValue,
            ConditionId = policy.ConditionId, ConditionFailedAction = policy.ConditionFailedAction
        };
        var devices = await db.DevicePolicies.AsNoTracking().Where(x => x.PolicyId == policyId).ToListAsync();
        state.Selected.AddRange(devices.Select(x => new PolicyTargetSelectionDto { Key = PolicyTargetKeyDto.Device(x.DeviceId) }));
        var groups = await db.GroupPolicies.AsNoTracking().Where(x => x.PolicyId == policyId).ToListAsync();
        var ous = await db.DirectoryOuPolicies.AsNoTracking().Where(x => x.PolicyId == policyId).ToListAsync();
        state.Active = await db.ActiveCompiledPolicies.AnyAsync(x => x.PolicyId == policyId);
        state.Selected.AddRange(groups.Select(x => new PolicyTargetSelectionDto { Key = PolicyTargetKeyDto.Group(x.DeviceGroupId), Order = x.PolicyOrder }));
        state.Selected.AddRange(ous.Select(x => new PolicyTargetSelectionDto { Key = PolicyTargetKeyDto.Ou(x.DirectorySourceId, x.OuGuid), Order = x.Order, IncludeDescendants = x.IncludeDescendants }));
        // Priority is saved independently of target membership and scope.
        state.AssignmentsVersion = Fingerprint(new
        {
            state.PolicyId,
            Targets = state.Selected.OrderBy(x => x.Key.DeviceId).ThenBy(x => x.Key.GroupId).ThenBy(x => x.Key.DirectorySourceId).ThenBy(x => x.Key.OuGuid)
                .Select(x => new { x.Key, x.IncludeDescendants })
        });
        state.Version = Fingerprint(new
        {
            state.PolicyId, state.ConditionId, state.ConditionFailedAction,
            Targets = state.Selected.OrderBy(x => x.Key.DeviceId).ThenBy(x => x.Key.GroupId).ThenBy(x => x.Key.DirectorySourceId).ThenBy(x => x.Key.OuGuid).Select(x => new { x.Key, Descendants = x.Key.IsOu && x.IncludeDescendants })
        });
        return state;
    }

    private static async Task<TargetPolicyStateDto?> ReadTargetStateAsync(CloneDeployDbContext db, PolicyTargetKeyDto key)
    {
        var exists = key.DeviceId is { } deviceId
            ? await db.Devices.AnyAsync(x => x.Id == deviceId)
            : key.GroupId is { } id
            ? await db.DeviceGroups.AnyAsync(x => x.Id == id)
            : await db.DirectoryObjects.AnyAsync(x => x.DirectorySourceId == key.DirectorySourceId && x.ObjectGuid == key.OuGuid && (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Domain));
        if (!exists) return null;
        var state = new TargetPolicyStateDto { Key = key };
        if (key.DeviceId is { } directDeviceId)
        {
            state.Assignments = await db.DevicePolicies.AsNoTracking().Where(x => x.DeviceId == directDeviceId)
                .OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.PolicyId)
                .Select(x => new TargetPolicyAssignmentDto { PolicyId = x.PolicyId, Name = x.Policy.Name, ExecutionPriority = x.Policy.ExecutionPriority }).ToListAsync();
        }
        else if (key.GroupId is { } groupId)
        {
            state.Assignments = await db.GroupPolicies.AsNoTracking().Where(x => x.DeviceGroupId == groupId).OrderBy(x => x.Policy!.ExecutionPriority).ThenBy(x => x.PolicyId)
                .Select(x => new TargetPolicyAssignmentDto { PolicyId = x.PolicyId, Name = x.Policy!.Name, ExecutionPriority = x.Policy!.ExecutionPriority }).ToListAsync();
        }
        else
        {
            state.Assignments = await (from link in db.DirectoryOuPolicies.AsNoTracking()
                join policy in db.Policies on link.PolicyId equals policy.Id
                where link.DirectorySourceId == key.DirectorySourceId && link.OuGuid == key.OuGuid
                orderby policy.ExecutionPriority, link.PolicyId
                select new TargetPolicyAssignmentDto { PolicyId = link.PolicyId, Name = policy.Name, ExecutionPriority = policy.ExecutionPriority, IncludeDescendants = link.IncludeDescendants }).ToListAsync();
        }

        state.Version = Fingerprint(new { key, Items = state.Assignments.OrderBy(x => x.PolicyId).Select(x => new { x.PolicyId, x.IncludeDescendants }) });
        return state;
    }

    private static async Task<List<PolicyTargetOptionDto>> ReadOptionsAsync(CloneDeployDbContext db)
    {
        var options = await DeploymentTargets.ReadOptionsAsync(db);
        var roots = await (from root in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on root.DirectorySourceId equals source.Id
            where root.Kind == DirectoryObjectKind.Domain && source.Provider == DirectoryProvider.ActiveDirectory
            orderby source.Name, root.Name
            select new { root.DirectorySourceId, root.ObjectGuid, root.Name, root.DistinguishedName, root.IsMissing, IsEnabled = source.IsEnabled && !source.ApplicationAccessOnly }).ToListAsync();
        options.AddRange(roots.Select(x => new PolicyTargetOptionDto(PolicyTargetKeyDto.Ou(x.DirectorySourceId, x.ObjectGuid),
            x.Name, x.DistinguishedName, !x.IsMissing && x.IsEnabled)));
        return options;
    }

    private static async Task<string?> ValidateSelectionsAsync(CloneDeployDbContext db, List<PolicyTargetSelectionDto> selected, List<PolicyTargetSelectionDto> previous)
    {
        if (selected.Any(x => !x.Key.IsValid) || selected.Select(x => x.Key).Distinct().Count() != selected.Count)
            return "Select each device, group or OU only once.";
        var options = await ReadOptionsAsync(db);
        options.AddRange(await ReadDeviceOptionsAsync(db, selected.Select(x => x.Key.DeviceId).OfType<int>().ToArray()));
        foreach (var item in selected)
        {
            if (options.Any(x => x.Key == item.Key && x.Available)) continue;
            if (previous.Any(x => x.Key == item.Key && x.IncludeDescendants == item.IncludeDescendants)) continue;
            return "A selected target is unavailable. Remove it or choose a currently available device, group or OU.";
        }

        return null;
    }

    public async Task<ServiceResult<List<PolicyTargetDeviceDto>>> SearchDevicesAsync(string? search)
    {
        var user = await GetViewUserAsync();
        if (user is null || !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<PolicyTargetDeviceDto>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text));
        }
        return ServiceResult<List<PolicyTargetDeviceDto>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100)
            .Select(x => new PolicyTargetDeviceDto(x.Id, x.Name)).ToListAsync());
    }

    private static async Task<List<PolicyTargetOptionDto>> ReadDeviceOptionsAsync(CloneDeployDbContext db, int[] ids)
    {
        var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.ArchivedAtUtc }).ToListAsync();
        return devices.Select(x => new PolicyTargetOptionDto(PolicyTargetKeyDto.Device(x.Id), x.Name,
            $"Device ID: {x.Id}", x.ArchivedAtUtc == null)).ToList();
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

    private async Task<ClaimsPrincipal?> GetViewUserAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded ? user : null;
    }

    private async Task<ClaimsPrincipal?> GetEditUserAsync()
    {
        var user = await GetViewUserAsync();
        if (user is null || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyEdit)).Succeeded)
            return null;
        return user;
    }

    private static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, Policy policy, object details)
    {
        var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Update,
            ObjectType = nameof(Policy), ObjectId = policy.Id, ObjectName = policy.Guid.ToString(), ObjectJson = JsonSerializer.Serialize(details)
        });
    }

}
