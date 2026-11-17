using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using CloneDeploy_Server.Backend.Services.Scheduler;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages device group details, labels, and manually selected members.
/// </summary>
public sealed class DeviceGroupAdministration(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    SchedulerQueue schedulerQueue,
    ILogger<DeviceGroupAdministration> logger)
{
    private async Task<bool> CanGroupAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded;
    }

    public async Task<ServiceResult<DeviceGroup>> GetByIdAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var group = await db.DeviceGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return group is null
            ? ServiceResult<DeviceGroup>.Fail(ErrorKeys.Common.NotFound, id)
            : ServiceResult<DeviceGroup>.Ok(group);
    }

    public async Task<ServiceResult<List<DeviceGroupRelationDto>>> GetRelationsAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView)) return ServiceResult<List<DeviceGroupRelationDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (!await db.DeviceGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<List<DeviceGroupRelationDto>>.Fail(ErrorKeys.Common.NotFound, id);
        return ServiceResult<List<DeviceGroupRelationDto>>.Ok(await DeviceGroupRelations.ReadAsync(db, id));
    }

    public async Task<ServiceResult<List<DeviceGroupAuditEntry>>> GetAuditAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView)) return ServiceResult<List<DeviceGroupAuditEntry>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (!await db.DeviceGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<List<DeviceGroupAuditEntry>>.Fail(ErrorKeys.Common.NotFound, id);
        var audits = await db.AuditLogs.AsNoTracking().Where(x => x.ObjectId == id && (x.ObjectType == nameof(DeviceGroup) || x.ObjectType == "Wake"))
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).ToListAsync();
        return ServiceResult<List<DeviceGroupAuditEntry>>.Ok(audits.Select(x => DeviceGroupAudit.Read(x, id)).OfType<DeviceGroupAuditEntry>().ToList());
    }

    private async Task AuditAsync(CloneDeployDbContext db, DeviceGroup group, AuditType operation, string section) =>
        DeviceGroupAudit.Add(db, (await authentication.GetAuthenticationStateAsync()).User, group.Id, group.Name, operation, section);

    public async Task<ServiceResult<long>> QueueRebuildAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit)) return ServiceResult<long>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        var result = await GetByIdAsync(id);
        if (!result.Success)
            return ServiceResult<long>.Fail(result.Error.Code, result.Error.Args);
        if (result.Data!.Type != DeviceGroupType.Dynamic || result.Data.QueryDefinitionJson is null)
            return ServiceResult<long>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.SaveDynamicCriteriaBeforeRecalculatingMembers));

        return await schedulerQueue.EnqueueTargetNowAsync(ScheduledTaskType.DynamicGroupRecalculation, id, default);
    }

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Forbidden);
        var selected = ids.Distinct().Order().ToArray();
        if (selected.Length == 0) return ServiceResult.Ok();

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DeviceGroupDependencies.LockAsync(db);
        // Use the same locks as membership rebuilds and group edits.
        foreach (var id in selected)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + id}, 0))");
        var groups = await db.DeviceGroups.Where(x => selected.Contains(x.Id)).ToListAsync();
        if (groups.Count != selected.Length)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound);
        if (groups.Any(x => x.Type is not (DeviceGroupType.Manual or DeviceGroupType.Dynamic)))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.OnlyLocalManualAndDynamicGroupsCanBeDeleted));

        var otherGroups = await db.DeviceGroups.AsNoTracking()
            .Where(x => !selected.Contains(x.Id) && x.QueryDefinitionJson != null).ToListAsync();
        foreach (var other in otherGroups)
        {
            try
            {
                if (DeviceGroupDependencies.References(DeviceQueryDefinitionDto.Parse(other.QueryDefinitionJson!)).Any(selected.Contains))
                    return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.Text0ReferencesASelectedDeviceGroupUpdateItsCriteria, (other.Name)));
            }
            catch (InvalidOperationException)
            {
                return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.RepairTheSavedCriteriaFor0BeforeDeletingGroups, (other.Name)));
            }
        }
        // Cascades remove group links, while devices, tags and policies remain intact.
        foreach (var group in groups) await AuditAsync(db, group, AuditType.Delete, "Group deleted");
        db.DeviceGroups.RemoveRange(groups);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<DeviceGroup>> CreateAsync(DeviceGroup input)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.EnterAGroupNameOfAtMost200Characters));
        if (input.Type is not (DeviceGroupType.Manual or DeviceGroupType.Dynamic))
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.SelectManualOrDynamicMembership));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DeviceGroupDependencies.LockAsync(db);
        var group = new DeviceGroup { Name = name, Description = input.Description?.Trim(), Type = input.Type };
        db.DeviceGroups.Add(group);
        await db.SaveChangesAsync();
        await AuditAsync(db, group, AuditType.Create, "Group created");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<DeviceGroup>.Ok(group);
    }

    public async Task<ServiceResult<List<int>>> GetTagIdsAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<List<int>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (!await db.DeviceGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<List<int>>.Fail(ErrorKeys.Common.NotFound, id);
        return ServiceResult<List<int>>.Ok(await db.DeviceGroupTags.Where(x => x.DeviceGroupId == id).Select(x => x.TagId).ToListAsync());
    }

    public async Task<ServiceResult> UpdateGeneralAsync(DeviceGroup input, IEnumerable<int> selectedTags)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Forbidden);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.EnterAGroupNameOfAtMost200Characters));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DeviceGroupDependencies.LockAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + input.Id}, 0))");
        var group = await db.DeviceGroups.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (group is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, input.Id);

        if (group.Type == DeviceGroupType.AllDevices && name != group.Name)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.TheAllDevicesGroupCannotBeRenamed));

        if (group.Type == DeviceGroupType.AllDevices && input.Type != group.Type)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.AllDevicesMembershipIsAutomaticAndCannotBeChanged));
        if (group.Type != DeviceGroupType.AllDevices && input.Type is not (DeviceGroupType.Manual or DeviceGroupType.Dynamic))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.SelectManualOrDynamicMembership));

        var tagIds = selectedTags.Distinct().ToArray();
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Length)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.OneOrMoreSelectedTagsNoLongerExist));

        group.Name = name;
        group.Description = input.Description?.Trim();
        if (group.Type != input.Type)
        {
            group.Type = input.Type;
            if (group.Type == DeviceGroupType.Dynamic && group.QueryDefinitionJson is not null)
            {
                var dependencyError = await DeviceGroupDependencies.ValidateAsync(db, group);
                if (dependencyError is not null)
                    return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, dependencyError);
            }
            group.MembershipEvaluatedAtUtc = null;
        }
        await db.DeviceGroupTags.Where(x => x.DeviceGroupId == group.Id).ExecuteDeleteAsync();
        db.DeviceGroupTags.AddRange(tagIds.Select(id => new DeviceGroupTag { DeviceGroupId = group.Id, TagId = id }));
        await AuditAsync(db, group, AuditType.Update, "General settings, membership mode and tags");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<QueryDeviceDto>>> GetMembersAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<List<QueryDeviceDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (!await db.DeviceGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<List<QueryDeviceDto>>.Fail(ErrorKeys.Common.NotFound, id);
        var ids = DeviceGroupMembershipQuery.DeviceIds(db, [id]);
        return ServiceResult<List<QueryDeviceDto>>.Ok(await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new QueryDeviceDto(x.Id, x.Name)).ToListAsync());
    }

    public async Task<ServiceResult<List<QueryDeviceDto>>> SearchDevicesAsync(string? search, CancellationToken ct = default)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult<List<QueryDeviceDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(text));
        }

        return ServiceResult<List<QueryDeviceDto>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(100)
            .Select(x => new QueryDeviceDto(x.Id, x.Name)).ToListAsync(ct));
    }

    public async Task<ServiceResult> UpdateMembersAsync(int id, DeviceGroupType type, DeviceQueryDefinitionDto definition,
        IEnumerable<int> selectedDevices, bool rebuild)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Forbidden);
        if (type is not (DeviceGroupType.Manual or DeviceGroupType.Dynamic))
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.SelectManualOrDynamicMembership));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DeviceGroupDependencies.LockAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + id}, 0))");
        var group = await db.DeviceGroups.SingleOrDefaultAsync(x => x.Id == id);
        if (group is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);

        if (group.Type == DeviceGroupType.AllDevices)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.AllDevicesMembershipIsAutomaticAndCannotBeChanged));

        string? json = null;
        var deviceIds = selectedDevices.Distinct().ToArray();
        if (type == DeviceGroupType.Dynamic)
        {
            var (query, error) = await DeviceQueryBuilder.BuildAsync(db, definition, default);
            if (error is not null)
                return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, error);
            _ = query!.ToQueryString();
            json = definition.ToJson();
        }
        else if (await db.Devices.CountAsync(x => deviceIds.Contains(x.Id) && x.ArchivedAtUtc == null &&
                                                  x.ProvisionStatus != DeviceProvisionStatus.Archived) != deviceIds.Length)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.OneOrMoreSelectedDevicesWereRemovedOrArchived));

        var changed = group.Type != type || group.QueryDefinitionJson != json;
        group.Type = type;
        group.QueryDefinitionJson = json;
        var dependencyError = await DeviceGroupDependencies.ValidateAsync(db, group);
        if (dependencyError is not null)
            return ServiceResult.Fail(ErrorKeys.DeviceGroup.Validation, dependencyError);
        if (changed || type == DeviceGroupType.Manual)
        {
            group.MembershipEvaluatedAtUtc = null;
            await db.DeviceGroupMemberships.Where(x => x.DeviceGroupId == id).ExecuteDeleteAsync();
        }

        if (type == DeviceGroupType.Manual)
            db.DeviceGroupMemberships.AddRange(deviceIds.Select(deviceId => new DeviceGroupMembership { DeviceGroupId = id, DeviceId = deviceId }));

        await AuditAsync(db, group, AuditType.Update, type == DeviceGroupType.Dynamic ? "Dynamic membership criteria" : "Manual membership");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        if (rebuild && type == DeviceGroupType.Dynamic)
        {
            try
            {
                var result = await schedulerQueue.EnqueueTargetNowAsync(ScheduledTaskType.DynamicGroupRecalculation, id, default);
                if (!result.Success)
                    return ServiceResult.Fail(ErrorKeys.DeviceGroup.RebuildNotQueued);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Membership for group {GroupId} was saved but its rebuild could not be queued", id);
                return ServiceResult.Fail(ErrorKeys.DeviceGroup.RebuildNotQueued);
            }
        }

        return ServiceResult.Ok();
    }
}
