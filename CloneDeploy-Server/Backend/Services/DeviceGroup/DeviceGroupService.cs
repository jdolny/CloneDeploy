using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for manual and dynamic device groups.
/// <summary>
/// Lets users manage device groups with manually selected or automatically matched members.
/// </summary>
public sealed class DeviceGroupService(
    DeviceGroupAdministration groups,
    DynamicDeviceGroupService dynamicGroups,
    ILogger<DeviceGroupService> logger,
    GroupPowerScheduleService powerSchedules)
{
    public Task<ServiceResult<List<DeviceGroupListItemDto>>> GetAsync() => ExecuteAsync(() => dynamicGroups.ListAsync());
    public Task<ServiceResult<long>> QueueRebuildAsync(int id) => ExecuteAsync(() => groups.QueueRebuildAsync(id));
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(() => groups.DeleteManyAsync(ids));
    public Task<ServiceResult<DeviceGroup>> GetByIdAsync(int id) => ExecuteAsync(() => groups.GetByIdAsync(id));
    public Task<ServiceResult<List<DeviceGroupRelationDto>>> GetRelationsAsync(int id) => ExecuteAsync(() => groups.GetRelationsAsync(id));
    public Task<ServiceResult<List<DeviceGroupAuditEntry>>> GetAuditAsync(int id) => ExecuteAsync(() => groups.GetAuditAsync(id));
    public Task<ServiceResult<DeviceGroup>> CreateAsync(DeviceGroup input) => ExecuteAsync(() => groups.CreateAsync(input));
    public Task<ServiceResult<List<int>>> GetTagIdsAsync(int id) => ExecuteAsync(() => groups.GetTagIdsAsync(id));

    public Task<ServiceResult> UpdateGeneralAsync(DeviceGroup input, IEnumerable<int> selectedTags) =>
        ExecuteAsync(() => groups.UpdateGeneralAsync(input, selectedTags));

    public Task<ServiceResult<List<QueryDeviceDto>>> GetMembersAsync(int id) => ExecuteAsync(() => groups.GetMembersAsync(id));

    public Task<ServiceResult<List<QueryDeviceDto>>> SearchDevicesAsync(string? search, CancellationToken ct = default) =>
        ExecuteAsync(() => groups.SearchDevicesAsync(search, ct));

    public Task<ServiceResult> UpdateMembersAsync(int id, DeviceGroupType type, DeviceQueryDefinitionDto definition,
        IEnumerable<int> selectedDevices, bool rebuild) =>
        ExecuteAsync(() => groups.UpdateMembersAsync(id, type, definition, selectedDevices, rebuild));

    public Task<ServiceResult<List<QueryDirectoryChoiceDto>>> DirectoryChoicesAsync() => ExecuteAsync(() => dynamicGroups.DirectoryChoicesAsync());
    public Task<ServiceResult<List<QueryValueChoiceDto>>> ValueChoicesAsync() => ExecuteAsync(() => dynamicGroups.ValueChoicesAsync());

    public Task<ServiceResult<QueryPreviewDto>> PreviewAsync(DeviceQueryDefinitionDto definition, CancellationToken ct = default) =>
        ExecuteAsync(() => dynamicGroups.PreviewAsync(definition, ct));

    public Task<ServiceResult<int>> SaveAsync(int id, string name, string? description, DeviceGroupType type,
        DeviceQueryDefinitionDto definition, bool rebuild, CancellationToken ct = default) =>
        ExecuteAsync(() => dynamicGroups.SaveAsync(id, name, description, type, definition, rebuild, ct));

    public ServiceResult<DeviceQueryDefinitionDto> ParseDefinition(string json) => dynamicGroups.ParseDefinition(json);

    public static IEnumerable<QueryScopeDto> QueryScopes => DynamicDeviceGroupService.QueryScopes;
    public static QueryScopeDto GetQueryScope(string scope) => DynamicDeviceGroupService.GetQueryScope(scope);

    public static System.Reflection.PropertyInfo GetQueryProperty(string scope, string field) =>
        DynamicDeviceGroupService.GetQueryProperty(scope, field);

    public static QueryOperator[] GetQueryOperators(string scope, string field) =>
        DynamicDeviceGroupService.GetQueryOperators(scope, field);

    public static string QueryLabel(string value) => DynamicDeviceGroupService.QueryLabel(value);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Group.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Group.Failed", null, operationName);

    public Task<List<DeviceGroupPowerSchedule>> GetPowerScheduleAsync(ClaimsPrincipal user, int groupId) =>
        powerSchedules.GetAsync(user, groupId);

    public Task SavePowerScheduleAsync(ClaimsPrincipal user, DeviceGroupPowerSchedule input) =>
        powerSchedules.SaveAsync(user, input);

    public Task DeletePowerScheduleAsync(ClaimsPrincipal user, int groupId, int id) =>
        powerSchedules.DeleteAsync(user, groupId, id);
}
