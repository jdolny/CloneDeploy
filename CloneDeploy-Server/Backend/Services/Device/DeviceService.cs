using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Agent.Contracts;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets users find and manage devices, their details, and device list preferences.
/// </summary>
public sealed class DeviceService(
    DeviceAdministration devices,
    DeviceColumnPreferencesService columns,
    ILogger<DeviceService> logger,
    DeviceActivityService activity,
    DeviceAttachmentService attachments,
    DeviceAuditService audit,
    DeviceDirectoryRelations relations,
    DeviceRenameService renaming,
    DeviceReportBuilderService reports,
    DeviceStatsService stats,
    SoftwareReportService softwareReports,
    UserDeviceReportService userReports,
    DeviceNotificationHistoryService notifications)
{
    public Task<ServiceResult<Device>> GetByIdAsync(int id) => ExecuteAsync(() => devices.GetByIdAsync(id));
    public Task<ServiceResult<Device>> GetDetailsAsync(int id, string tab) => ExecuteAsync(() => devices.GetDetailsAsync(id, tab));
    public Task<ServiceResult<List<Device>>> GetDevicesAsync(DeviceFilterDto? filter = null) => ExecuteAsync(() => devices.GetDevicesAsync(filter));
    public Task<ServiceResult<List<DeviceSearchResultDto>>> SearchDevices(DeviceFilterDto filter) => ExecuteAsync(() => devices.SearchDevices(filter));
    public Task<ServiceResult<Device>> CreateDeviceAsync(string name) => ExecuteAsync(() => devices.CreateDeviceAsync(name));
    public Task<ServiceResult<List<Device>>> CreateDevicesAsync(string names) => ExecuteAsync(() => devices.CreateDevicesAsync(names));
    public Task<ServiceResult> ArchiveDeviceAsync(int id) => ExecuteAsync(() => devices.ArchiveDeviceAsync(id));
    public Task<ServiceResult> RestoreDeviceAsync(int id) => ExecuteAsync(() => devices.RestoreDeviceAsync(id));
    public Task<ServiceResult> DeleteDeviceAsync(int id) => ExecuteAsync(() => devices.DeleteDeviceAsync(id));
    public Task<ServiceResult<Comment>> AddCommentAsync(int deviceId, string text) =>
        ExecuteAsync(() => devices.AddCommentAsync(deviceId, text));
    public Task<ServiceResult> UpdateGeneralAsync(int id, string? description, IEnumerable<int> tagIds) =>
        ExecuteAsync(() => devices.UpdateGeneralAsync(id, description, tagIds));
    public Task<ServiceResult> SaveCustomAttributesAsync(int id, IReadOnlyDictionary<int, string> values) => ExecuteAsync(() => devices.SaveCustomAttributesAsync(id, values));
    public Task<ServiceResult<List<AppUserGridColumn>>> LoadColumnsAsync() => ExecuteAsync(() => columns.LoadAsync());
    public Task<ServiceResult> SaveColumnsAsync(IReadOnlyList<AppUserGridColumn> preferences) => ExecuteAsync(() => columns.SaveAsync(preferences));
    public Task<ServiceResult<List<CustomAttribute>>> GetColumnAttributesAsync() => ExecuteAsync(() => columns.GetCustomAttributesAsync());
    public Task<ServiceResult> LoadAttributeValuesAsync(IEnumerable<DeviceSearchResultDto> devices, int[] attributeIds) => ExecuteAsync(() => columns.LoadAttributeValuesAsync(devices, attributeIds));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Device.Failed", null, operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, "Device.Failed", null, operationName);

    public Task<IDisposable> SubscribeAsync(int[] ids, Func<IReadOnlyList<DeviceActivitySnapshot>, Task> receive) =>
        activity.SubscribeAsync(ids, receive);

    public Task<ServiceResult<Attachment>> AttachmentAttachUploadAsync(int deviceId, Guid uploadId) =>
        ExecuteAsync(() => attachments.AttachUploadAsync(deviceId, uploadId));

    public Task<ServiceResult<List<DeviceAuditEntry>>> GetAuditAsync(int deviceId) =>
        ExecuteAsync(() => audit.GetAsync(deviceId));

    public Task<List<DeviceDirectoryRelation>> ReadDirectoryRelationsAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default) =>
        relations.ReadAsync(user, deviceId, ct);

    public Task<List<ManagedCredentialDto>> GetRenameCredentialsAsync(ClaimsPrincipal user) =>
        renaming.GetRenameCredentialsAsync(user);

    public Task<DeviceRenameResult> RenameAsync(ClaimsPrincipal user, int deviceId, string name, bool renameConnectedWindows, int? credentialId = null, CancellationToken ct = default) =>
        renaming.RenameAsync(user, deviceId, name, renameConnectedWindows, credentialId, ct);

    public Task<ServiceResult<DeviceReportOptions>> ReportOptionsAsync() =>
        ExecuteAsync(() => reports.OptionsAsync());

    public Task<ServiceResult<List<SavedDeviceReport>>> ListReportAsync() =>
        ExecuteAsync(() => reports.ListAsync());

    public Task<ServiceResult<SavedDeviceReport>> SaveReportAsync(int id, Guid revision, string name, DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => reports.SaveAsync(id, revision, name, definition, columns));

    public Task<ServiceResult<bool>> DeleteReportAsync(int id, Guid revision) =>
        ExecuteAsync(() => reports.DeleteAsync(id, revision));

    public Task<ServiceResult<DeviceReportPreview>> PreviewReportAsync(DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => reports.PreviewAsync(definition, columns));

    public Task<ServiceResult<string>> ExportReportAsync(DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => reports.ExportAsync(definition, columns));

    public Task<ServiceResult<DeviceStatsSnapshot>> GetStatsAsync() =>
        ExecuteAsync(() => stats.GetAsync());

    public Task<ServiceResult<List<SoftwareReportRow>>> ListSoftwareReportAsync() =>
        ExecuteAsync(() => softwareReports.ListAsync());

    public Task<ServiceResult<List<SoftwareReportDevice>>> SoftwareReportDevicesAsync(int softwareId) =>
        ExecuteAsync(() => softwareReports.DevicesAsync(softwareId));

    public Task<ServiceResult<List<SavedUserDeviceReport>>> ListUserReportAsync() =>
        ExecuteAsync(() => userReports.ListAsync());

    public Task<ServiceResult<SavedUserDeviceReport>> SaveUserReportAsync(int id, Guid revision, string name, UserDeviceReportQuery definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => userReports.SaveAsync(id, revision, name, definition, columns));

    public Task<ServiceResult<bool>> DeleteUserReportAsync(int id, Guid revision) =>
        ExecuteAsync(() => userReports.DeleteAsync(id, revision));

    public Task<ServiceResult<DeviceReportPreview>> PreviewUserReportAsync(UserDeviceReportQuery definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => userReports.PreviewAsync(definition, columns));

    public Task<ServiceResult<string>> ExportUserReportAsync(UserDeviceReportQuery definition, IReadOnlyList<string> columns) =>
        ExecuteAsync(() => userReports.ExportAsync(definition, columns));

    public Task<ServiceResult<List<DeviceNotificationReceipt>>> GetNotificationHistoryAsync(int deviceId) =>
        ExecuteAsync(() => notifications.GetAsync(deviceId));

    public static IReadOnlyList<string> DefaultReportColumns => DeviceReportColumns.Defaults;
    public static IReadOnlyList<DeviceReportColumn> UserReportColumns => UserDeviceReportService.Columns;
    public static IReadOnlyList<string> DefaultUserReportColumns => UserDeviceReportService.Defaults;
    public static IReadOnlyList<DeviceReportColumn> SelfServiceReportColumns => UserDeviceReportService.SelfServiceColumns;
    public static IReadOnlyList<string> DefaultSelfServiceReportColumns => UserDeviceReportService.SelfServiceDefaults;
}
