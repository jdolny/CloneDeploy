using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Manages automatic group rules and previews which devices match them.
/// </summary>
public sealed class DynamicDeviceGroupService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    CloneDeploy_Server.Backend.Services.Scheduler.SchedulerQueue schedulerQueue,
    ILogger<DynamicDeviceGroupService> logger)
{
    private async Task<bool> CanGroupAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded;
    }

    public async Task<ServiceResult<List<DeviceGroupListItemDto>>> ListAsync()
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<List<DeviceGroupListItemDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var allDevices = DeviceGroupMembershipQuery.AllDevices(db);
        return ServiceResult<List<DeviceGroupListItemDto>>.Ok(await db.DeviceGroups.AsNoTracking().OrderBy(x => x.Name).Select(x => new DeviceGroupListItemDto(x.Id, x.Name, x.Type,
            x.Type == DeviceGroupType.AllDevices ? allDevices.Count() : db.DeviceGroupMemberships.Count(m => m.DeviceGroupId == x.Id), x.MembershipEvaluatedAtUtc)
        {
            TagIds = db.DeviceGroupTags.Where(t => t.DeviceGroupId == x.Id).Select(t => t.TagId).ToList()
        }).ToListAsync());
    }

    public async Task<ServiceResult<DeviceGroup>> GetAsync(int id)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var group = await db.DeviceGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (group is null)
            return ServiceResult<DeviceGroup>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DynamicDeviceGroupService.DeviceGroupNotFound));
        return ServiceResult<DeviceGroup>.Ok(group);
    }

    public async Task<ServiceResult<List<QueryDirectoryChoiceDto>>> DirectoryChoicesAsync()
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<List<QueryDirectoryChoiceDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var entries = await (from o in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on o.DirectorySourceId equals source.Id
            where (source.IsEnabled && !source.ApplicationAccessOnly) && !o.IsMissing && o.Kind != DirectoryObjectKind.User && o.Kind != DirectoryObjectKind.Computer
            orderby source.Name, o.DistinguishedName
            select new { o.DirectorySourceId, o.ObjectGuid, o.ParentGuid, o.Kind, o.Name, o.DistinguishedName, SourceName = source.Name }).ToListAsync();
        var byId = entries.ToDictionary(x => (x.DirectorySourceId, x.ObjectGuid));
        return ServiceResult<List<QueryDirectoryChoiceDto>>.Ok(entries.Where(x => x.Kind is DirectoryObjectKind.Group or DirectoryObjectKind.OrganizationalUnit).Select(entry =>
        {
            var names = new List<string> { entry.Name };
            var seen = new HashSet<Guid> { entry.ObjectGuid };
            var parent = entry.ParentGuid;
            while (parent is { } guid && seen.Add(guid) && byId.TryGetValue((entry.DirectorySourceId, guid), out var ancestor))
            {
                if (ancestor.Kind != DirectoryObjectKind.Domain) names.Add(ancestor.Name);
                parent = ancestor.ParentGuid;
            }

            names.Reverse();
            var path = entry.SourceName + " › " + string.Join(" › ", names);
            return new QueryDirectoryChoiceDto(entry.DirectorySourceId, entry.ObjectGuid, entry.Kind,
                    entry.Kind == DirectoryObjectKind.Group ? entry.Name + " — " + entry.SourceName : path)
                { Path = path, DistinguishedName = entry.DistinguishedName };
        }).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<ServiceResult<List<QueryValueChoiceDto>>> ValueChoicesAsync()
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupView))
            return ServiceResult<List<QueryValueChoiceDto>>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var canViewAttributes = await CanGroupAsync(Constants.Permissions.CustomAttributeView);
        var choices = await db.CustomAttributes.AsNoTracking().Where(x => canViewAttributes).OrderBy(x => x.Name)
            .Select(x => new QueryValueChoiceDto("CustomAttributeId", x.Id.ToString(), x.Name)).ToListAsync();
        choices.AddRange(await db.ScriptActions.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new QueryValueChoiceDto("ScriptActionId", x.Id.ToString(), x.Name)).ToListAsync());
        choices.AddRange(await db.DeviceGroups.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new QueryValueChoiceDto("DeviceGroup", x.Id.ToString(), x.Name)).ToListAsync());
        return ServiceResult<List<QueryValueChoiceDto>>.Ok(choices);
    }

    public async Task<ServiceResult<QueryPreviewDto>> PreviewAsync(DeviceQueryDefinitionDto definition, CancellationToken ct = default)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult<QueryPreviewDto>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(30);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var (query, error) = await DeviceQueryBuilder.BuildAsync(db, definition, ct);
        if (query is null)
            return ServiceResult<QueryPreviewDto>.Fail(ErrorKeys.DeviceGroup.Validation, error!);
        return ServiceResult<QueryPreviewDto>.Ok(new QueryPreviewDto(await query.CountAsync(ct), await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Take(100).Select(x => new QueryDeviceDto(x.Id, x.Name)).ToListAsync(ct)));
    }

    public async Task<ServiceResult<int>> SaveAsync(int id, string name, string? description, DeviceGroupType type,
        DeviceQueryDefinitionDto definition, bool rebuild, CancellationToken ct = default)
    {
        if (!await CanGroupAsync(Constants.Permissions.DeviceGroupEdit))
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Forbidden);
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.EnterAGroupNameOfAtMost200Characters));
        if (type is not (DeviceGroupType.Manual or DeviceGroupType.Dynamic))
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DeviceGroupAdministration.SelectManualOrDynamicMembership));
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(30);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await DeviceGroupDependencies.LockAsync(db, ct);
        // Serialize edits/rebuilds for this group across application instances.
        if (id != 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + id}, 0))", ct);
        var group = id == 0 ? new DeviceGroup { Name = name, Type = type } : await db.DeviceGroups.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (group is null)
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DynamicDeviceGroupService.DeviceGroupNotFound));
        if (group.Type == DeviceGroupType.AllDevices)
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, UiText.Get(LocKeys.Ui.DynamicDeviceGroupService.TheBuiltInAllDevicesGroupCannotBeRedefined));
        IQueryable<Device>? query = null;
        if (type == DeviceGroupType.Dynamic)
        {
            var built = await DeviceQueryBuilder.BuildAsync(db, definition, ct);
            if (built.Error is not null)
                return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, built.Error);
            query = built.Query;
        }

        if (query != null) _ = query.ToQueryString(); // Validate translation before saving a definition.
        var json = type == DeviceGroupType.Dynamic ? definition.ToJson() : null;
        var changed = group.Type != type || group.QueryDefinitionJson != json;
        group.Name = name;
        group.Description = description;
        group.Type = type;
        group.QueryDefinitionJson = json;
        var dependencyError = await DeviceGroupDependencies.ValidateAsync(db, group, ct);
        if (dependencyError is not null)
            return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.Validation, dependencyError);
        if (changed)
            group.MembershipEvaluatedAtUtc = null;
        if (id == 0)
            db.DeviceGroups.Add(group);
        await db.SaveChangesAsync(ct);
        if (changed)
        {
            // Old computed memberships must not silently remain authoritative for a different definition/mode.
            await db.DeviceGroupMemberships.Where(x => x.DeviceGroupId == group.Id).ExecuteDeleteAsync(ct);
        }

        DeviceGroupAudit.Add(db, (await authentication.GetAuthenticationStateAsync()).User, group.Id, group.Name,
            id == 0 ? AuditType.Create : AuditType.Update, "Group settings and membership criteria");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (rebuild && query != null)
        {
            try
            {
                var queued = await schedulerQueue.EnqueueTargetNowAsync(ScheduledTaskType.DynamicGroupRecalculation, group.Id, ct);
                if (!queued.Success)
                    return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.RebuildNotQueued);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(exception, "Dynamic group {GroupId} was saved but its rebuild could not be queued", group.Id);
                return ServiceResult<int>.Fail(ErrorKeys.DeviceGroup.RebuildNotQueued);
            }
        }

        return ServiceResult<int>.Ok(group.Id);
    }

    public static IEnumerable<QueryScopeDto> QueryScopes => DeviceQueryCatalog.AvailableScopes;
    public static QueryScopeDto GetQueryScope(string scope) => DeviceQueryCatalog.Get(scope);
    public static System.Reflection.PropertyInfo GetQueryProperty(string scope, string field) => DeviceQueryCatalog.Property(scope, field);
    public static QueryOperator[] GetQueryOperators(string scope, string field) => DeviceQueryCatalog.Operators(scope, field);
    public static string QueryLabel(string value) => DeviceQueryCatalog.Label(value);

    public ServiceResult<DeviceQueryDefinitionDto> ParseDefinition(string json)
    {
        try
        {
            return ServiceResult<DeviceQueryDefinitionDto>.Ok(DeviceQueryDefinitionDto.Parse(json));
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult<DeviceQueryDefinitionDto>.Fail(ErrorKeys.DeviceGroup.Validation, exception.Message);
        }
    }
}
