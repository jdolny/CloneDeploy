using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for action editing, lifecycle changes, and files.
/// <summary>
/// Lets users create and manage actions and their attached files.
/// </summary>
public sealed class ActionService(
    ActionLifecycleService lifecycle,
    ActionFileService files,
    CommandActionService command,
    ScriptActionService script,
    SoftwareActionService software,
    FileActionService file,
    PrinterActionService printer,
    NotificationActionService message,
    DomainJoinActionService domainJoin,
    WindowsUpdateActionService windowsUpdate,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    ILogger<ActionService> logger)
{
    public Task<ServiceResult<List<ActionRelationDto>>> GetRelationsAsync(Guid guid) => ExecuteAsync(() => lifecycle.GetRelationsAsync(guid));
    public Task<ServiceResult<List<ActionExecutionHistoryDto>>> GetExecutionHistoryAsync(Guid guid) => ExecuteAsync(() => lifecycle.GetExecutionHistoryAsync(guid));

    public Task<ServiceResult<List<SoftwareUninstallChoice>>> SearchSoftwareInventoryAsync(string? search) => ExecuteAsync(async () => ServiceResult<List<SoftwareUninstallChoice>>.Ok(await software.SearchInventoryAsync(search)));
    public Task<ServiceResult<List<CommandAction>>> GetCommandActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => command.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<CommandAction>> GetCommandAsync(int id) => ExecuteAsync(() => command.GetByIdAsync(id));
    public Task<ServiceResult<CommandAction>> CreateCommandAsync(string name, string? description) => ExecuteAsync(() => command.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralCommandAsync(CommandAction action) => ExecuteAsync(() => command.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateCommandAsync(CommandAction action) => ExecuteAsync(() => command.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByCommandAsync(Guid guid) => ExecuteAsync(() => command.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditCommandAsync(Guid guid) => ExecuteAsync(() => command.GetAuditAsync(guid));
    public Task<ServiceResult<List<ScriptAction>>> GetScriptActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => script.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<ScriptAction>> GetScriptAsync(int id) => ExecuteAsync(() => script.GetByIdAsync(id));
    public Task<ServiceResult<ScriptAction>> CreateScriptAsync(string name, string? description) => ExecuteAsync(() => script.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralScriptAsync(ScriptAction action) => ExecuteAsync(() => script.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateScriptAsync(ScriptAction action) => ExecuteAsync(() => script.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByScriptAsync(Guid guid) => ExecuteAsync(() => script.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditScriptAsync(Guid guid) => ExecuteAsync(() => script.GetAuditAsync(guid));
    public Task<ServiceResult<List<SoftwareAction>>> GetSoftwareActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(async () => ServiceResult<List<SoftwareAction>>.Ok(await software.GetAsync(searchText, includeArchived)));
    public Task<ServiceResult<SoftwareAction>> GetSoftwareAsync(int id) => ExecuteAsync(() => software.GetByIdAsync(id));
    public Task<ServiceResult<SoftwareAction>> CreateSoftwareAsync(SoftwareAction softwareAction) => ExecuteAsync(() => software.CreateAsync(softwareAction));
    public Task<ServiceResult> UpdateSoftwareAsync(SoftwareAction action) => ExecuteAsync(() => software.UpdateAsync(action));
    public Task<ServiceResult> UpdateGeneralSoftwareAsync(SoftwareAction action) => ExecuteAsync(() => software.UpdateGeneralAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedBySoftwareAsync(Guid guid) => ExecuteAsync(() => software.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditSoftwareAsync(Guid guid) => ExecuteAsync(() => software.GetAuditAsync(guid));
    public Task<ServiceResult<List<FileAction>>> GetFileActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => file.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<FileAction>> GetFileAsync(int id) => ExecuteAsync(() => file.GetByIdAsync(id));
    public Task<ServiceResult<FileAction>> CreateFileAsync(string name, string? description) => ExecuteAsync(() => file.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralFileAsync(FileAction action) => ExecuteAsync(() => file.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateFileAsync(FileAction action) => ExecuteAsync(() => file.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByFileAsync(Guid guid) => ExecuteAsync(() => file.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditFileAsync(Guid guid) => ExecuteAsync(() => file.GetAuditAsync(guid));
    public Task<ServiceResult<List<PrinterAction>>> GetPrinterActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => printer.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<PrinterAction>> GetPrinterAsync(int id) => ExecuteAsync(() => printer.GetByIdAsync(id));
    public Task<ServiceResult<PrinterAction>> CreatePrinterAsync(string name, string? description) => ExecuteAsync(() => printer.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralPrinterAsync(PrinterAction action) => ExecuteAsync(() => printer.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdatePrinterAsync(PrinterAction action) => ExecuteAsync(() => printer.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByPrinterAsync(Guid guid) => ExecuteAsync(() => printer.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditPrinterAsync(Guid guid) => ExecuteAsync(() => printer.GetAuditAsync(guid));
    public Task<ServiceResult<List<PolicyTargetDirectoryNodeDto>>> GetDomainJoinDirectoryOusAsync() => ExecuteAsync(() => domainJoin.GetDirectoryOusAsync());
    public Task<ServiceResult<List<DomainJoinRuleChoice>>> GetDomainJoinRuleSetsAsync() => ExecuteAsync(() => domainJoin.GetRuleSetsAsync());
    public Task<ServiceResult<List<DomainJoinAction>>> GetDomainJoinActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => domainJoin.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<DomainJoinAction>> GetDomainJoinAsync(int id) => ExecuteAsync(() => domainJoin.GetByIdAsync(id));
    public Task<ServiceResult<DomainJoinAction>> CreateDomainJoinAsync(string name, string? description) => ExecuteAsync(() => domainJoin.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralDomainJoinAsync(DomainJoinAction action) => ExecuteAsync(() => domainJoin.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateDomainJoinAsync(DomainJoinAction action) => ExecuteAsync(() => domainJoin.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByDomainJoinAsync(Guid guid) => ExecuteAsync(() => domainJoin.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditDomainJoinAsync(Guid guid) => ExecuteAsync(() => domainJoin.GetAuditAsync(guid));
    public Task<ServiceResult<List<WindowsUpdateAction>>> GetWindowsUpdateActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => windowsUpdate.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<WindowsUpdateAction>> GetWindowsUpdateAsync(int id) => ExecuteAsync(() => windowsUpdate.GetByIdAsync(id));
    public Task<ServiceResult<WindowsUpdateAction>> CreateWindowsUpdateAsync(string name, string? description) => ExecuteAsync(() => windowsUpdate.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralWindowsUpdateAsync(WindowsUpdateAction action) => ExecuteAsync(() => windowsUpdate.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateWindowsUpdateAsync(WindowsUpdateAction action) => ExecuteAsync(() => windowsUpdate.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByWindowsUpdateAsync(Guid guid) => ExecuteAsync(() => windowsUpdate.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditWindowsUpdateAsync(Guid guid) => ExecuteAsync(() => windowsUpdate.GetAuditAsync(guid));
    public Task<ServiceResult<List<NotificationAction>>> GetNotificationActionsAsync(string? searchText = null, bool includeArchived = false) => ExecuteAsync(() => message.GetAsync(searchText, includeArchived));
    public Task<ServiceResult<NotificationAction>> GetNotificationAsync(int id) => ExecuteAsync(() => message.GetByIdAsync(id));
    public Task<ServiceResult<NotificationAction>> CreateNotificationAsync(string name, string? description) => ExecuteAsync(() => message.CreateAsync(name, description));
    public Task<ServiceResult> UpdateGeneralNotificationAsync(NotificationAction action) => ExecuteAsync(() => message.UpdateGeneralAsync(action));
    public Task<ServiceResult> UpdateNotificationAsync(NotificationAction action) => ExecuteAsync(() => message.UpdateAsync(action));
    public Task<ServiceResult<List<Policy>>> GetUsedByNotificationAsync(Guid guid) => ExecuteAsync(() => message.GetUsedByAsync(guid));
    public Task<ServiceResult<List<AuditLog>>> GetAuditNotificationAsync(Guid guid) => ExecuteAsync(() => message.GetAuditAsync(guid));
    public Task<ServiceResult<List<ActionFileItemDto>>> GetActionFilesAsync(Guid action) => ExecuteAsync(() => files.GetAsync(action));
    public Task<ServiceResult<int?>> GetFileRefreshScheduleAsync(Guid action) => ExecuteAsync(() => files.GetRefreshScheduleAsync(action));
    public Task<ServiceResult> SaveFileRefreshScheduleAsync(Guid action, int? scheduleId) => ExecuteAsync(() => files.SaveRefreshScheduleAsync(action, scheduleId));
    public Task<ServiceResult> AttachUploadAsync(Guid action, Guid uploadId) => ExecuteAsync(() => files.AttachUploadAsync(action, uploadId));
    public Task<ServiceResult> SaveExternalFileAsync(Guid action, int? id, string name, string url, string? expectedHash, bool autoRefresh) => ExecuteAsync(() => files.SaveExternalAsync(action, id, name, url, expectedHash, autoRefresh));
    public Task<ServiceResult> QueueFileAsync(Guid action, int id) => ExecuteAsync(() => files.QueueAsync(action, id));
    public Task<ServiceResult> CancelFileAsync(Guid action, int id) => ExecuteAsync(() => files.CancelAsync(action, id));
    public Task<ServiceResult> DeleteFileAsync(Guid action, int id) => ExecuteAsync(() => files.DeleteAsync(action, id));
    public Task<ServiceResult> ArchiveAsync(Guid guid) => ExecuteAsync(() => lifecycle.ArchiveAsync(guid));
    public Task<ServiceResult> ArchiveManyAsync(IEnumerable<Guid> guids) => ExecuteAsync(() => lifecycle.ArchiveManyAsync(guids));
    public Task<ServiceResult> RestoreAsync(Guid guid) => ExecuteAsync(() => lifecycle.RestoreAsync(guid));
    public Task<ServiceResult> DeleteAsync(Guid guid) => ExecuteAsync(() => lifecycle.DeleteAsync(guid));
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<Guid> guids) => ExecuteAsync(() => lifecycle.DeleteManyAsync(guids));
    public Task<ServiceResult<List<Tag>>> GetTagsAsync() => ExecuteAsync(() => command.GetTagsAsync());
    public Task<ServiceResult<List<ManagedCredential>>> GetCredentialsAsync() => ExecuteAsync(() => command.GetCredentialsAsync());
    public const string MsiArguments = SoftwareInstallerSelection.MsiArguments;
    public static string InstallerExtension(SoftwareAction action) => SoftwareInstallerSelection.Extension(action);
    public static bool MatchesInstaller(SoftwareAction action, string name) => SoftwareInstallerSelection.Matches(action, name);
    public static bool IsInstallerReady(ActionFileItemDto file) => SoftwareInstallerSelection.Ready(file);
    public static ServiceResult SelectInstaller(SoftwareAction action, ActionFileItemDto file) => SoftwareInstallerSelection.Select(action, file);
    public static void SynchronizeInstaller(SoftwareAction action, IReadOnlyList<ActionFileItemDto> files) => SoftwareInstallerSelection.Synchronize(action, files);

    private async Task<bool> CanExecuteAsync(string operationName)
    {
        var permission = operationName switch
        {
            nameof(GetRelationsAsync) => Constants.Permissions.PolicyView,
            nameof(GetExecutionHistoryAsync) => Constants.Permissions.PolicyView,
            nameof(SearchSoftwareInventoryAsync) => Constants.Permissions.PolicyView,
            nameof(GetCommandActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetCommandAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByCommandAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditCommandAsync) => Constants.Permissions.PolicyView,
            nameof(GetScriptActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetScriptAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByScriptAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditScriptAsync) => Constants.Permissions.PolicyView,
            nameof(GetSoftwareActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetSoftwareAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedBySoftwareAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditSoftwareAsync) => Constants.Permissions.PolicyView,
            nameof(GetFileActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetFileAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByFileAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditFileAsync) => Constants.Permissions.PolicyView,
            nameof(GetPrinterActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetPrinterAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByPrinterAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditPrinterAsync) => Constants.Permissions.PolicyView,
            nameof(GetDomainJoinDirectoryOusAsync) => Constants.Permissions.PolicyView,
            nameof(GetDomainJoinRuleSetsAsync) => Constants.Permissions.PolicyView,
            nameof(GetDomainJoinActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetDomainJoinAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByDomainJoinAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditDomainJoinAsync) => Constants.Permissions.PolicyView,
            nameof(GetWindowsUpdateActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetWindowsUpdateAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByWindowsUpdateAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditWindowsUpdateAsync) => Constants.Permissions.PolicyView,
            nameof(GetNotificationActionsAsync) => Constants.Permissions.PolicyView,
            nameof(GetNotificationAsync) => Constants.Permissions.PolicyView,
            nameof(GetUsedByNotificationAsync) => Constants.Permissions.PolicyView,
            nameof(GetAuditNotificationAsync) => Constants.Permissions.PolicyView,
            nameof(GetActionFilesAsync) => Constants.Permissions.PolicyView,
            nameof(GetFileRefreshScheduleAsync) => Constants.Permissions.PolicyView,
            nameof(GetTagsAsync) => Constants.Permissions.TagView,
            nameof(GetCredentialsAsync) => Constants.Permissions.PolicyView,
            _ => Constants.Permissions.PolicyEdit
        };
        return await PolicyActionAccess.UserAsync(authentication, authorization, permission) is not null;
    }

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(async () =>
        {
            if (!await CanExecuteAsync(operationName)) return ServiceResult.Fail(ErrorKeys.Action.Forbidden);
            return await operation();
        }, logger, ErrorKeys.Action.Failed, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(async () =>
        {
            if (!await CanExecuteAsync(operationName)) return ServiceResult<T>.Fail(ErrorKeys.Action.Forbidden);
            return await operation();
        }, logger, ErrorKeys.Action.Failed, operationName: operationName);

}
