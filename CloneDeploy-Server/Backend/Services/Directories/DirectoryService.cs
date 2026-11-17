using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using CloneDeploy_Server.Backend.Enum;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// UI entry point for connection management, directory browsing, and manual synchronization.
/// <summary>
/// Lets users manage directory connections, browse their contents, and synchronize device information.
/// </summary>
public sealed class DirectoryService(
    DirectorySourceService sources,
    ActiveDirectoryService activeDirectory,
    ILogger<DirectoryService> logger,
    DirectoryUserActivityService activity,
    EntraAdministration entra, SecurityActivity? security = null)
{
    public Task<ServiceResult<List<DirectorySource>>> GetSourcesAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => sources.GetSourcesAsync(ct));

    public Task<ServiceResult<int>> CreateAsync(DirectorySource input, CancellationToken ct = default) =>
        ExecuteAsync(() => sources.CreateAsync(input, ct));

    public Task<ServiceResult<DirectorySource>> NewLdapSourceAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => sources.NewLdapSourceAsync(ct));

    public Task<ServiceResult<List<DirectorySettingChoiceDto>>> GetCredentialsAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => sources.GetCredentialsAsync(ct));

    public Task<ServiceResult<List<DirectorySettingChoiceDto>>> GetSchedulesAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => sources.GetSchedulesAsync(ct));

    public Task<ServiceResult<int>> SaveLdapSourceAsync(DirectorySource input, CancellationToken ct = default) =>
        ExecuteAsync(() => sources.SaveLdapSourceAsync(input, ct));

    public Task<ServiceResult<int>> SaveEntraSourceAsync(DirectorySource input, CancellationToken ct = default) =>
        ExecuteAsync(() => sources.SaveEntraSourceAsync(input, ct));

    public Task<ServiceResult> DeleteAsync(int sourceId, CancellationToken ct = default) =>
        ExecuteAsync(() => sources.DeleteAsync(sourceId, ct));

    public Task<ServiceResult<DirectorySyncSummaryDto>> SyncAsync(int sourceId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => activeDirectory.SyncAsync(sourceId, cancellationToken));

    public Task<ServiceResult<List<DirectoryObject>>> GetObjectsAsync(int directorySourceId, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetObjectsAsync(directorySourceId, ct));

    public Task<ServiceResult<DirectoryUserHistoryPage>> GetUserSelfServiceHistoryAsync(
        int directorySourceId, Guid objectGuid, int page, int pageSize, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetUserSelfServiceHistoryAsync(directorySourceId, objectGuid, page, pageSize, ct));

    public Task<ServiceResult<DirectoryObject?>> GetUserAsync(int directorySourceId, Guid objectGuid, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetUserAsync(directorySourceId, objectGuid, ct));

    public Task<ServiceResult<List<DirectoryObject>>> GetUsersAsync(int directorySourceId, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetUsersAsync(directorySourceId, ct));

    public Task<ServiceResult<DirectorySyncState?>> GetSyncStateAsync(int directoryId, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetSyncStateAsync(directoryId, ct));

    public Task<ServiceResult<List<DirectoryPolicyChoiceDto>>> GetPolicyChoicesAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetPolicyChoicesAsync(ct));

    public Task<ServiceResult<List<DirectoryPolicyLinkDto>>> GetOuPoliciesAsync(int directorySourceId, Guid ouGuid, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.GetOuPoliciesAsync(directorySourceId, ouGuid, ct));

    public Task<ServiceResult> AssignPolicyAsync(int directorySourceId, Guid ouGuid, int policyId, bool includeDescendants, int order, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.AssignPolicyAsync(directorySourceId, ouGuid, policyId, includeDescendants, order, ct));

    public Task<ServiceResult> RemovePolicyAsync(int directorySourceId, Guid ouGuid, int policyId, CancellationToken ct = default) =>
        ExecuteAsync(() => activeDirectory.RemovePolicyAsync(directorySourceId, ouGuid, policyId, ct));

    private async Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "Directory.Failed", null, operationName);
        if (result.Success) await RecordChangeAsync(operationName);
        return result;
    }

    private async Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "Directory.Failed", null, operationName);
        if (result.Success) await RecordChangeAsync(operationName);
        return result;
    }

    private Task RecordChangeAsync(string operation)
    {
        var label = operation switch
        {
            nameof(CreateAsync) => "Directory connection created", nameof(DeleteAsync) => "Directory connection deleted",
            nameof(SaveLdapSourceAsync) => "LDAP connection settings saved", nameof(SaveEntraSourceAsync) => "Entra connection settings saved",
            nameof(SaveEntraAsync) => "Entra authentication settings saved", nameof(EntraGenerateAsync) => "Entra certificate generated",
            nameof(EntraActivateAsync) => "Entra certificate activated", nameof(EntraDiscardPendingAsync) => "Pending Entra certificate discarded",
            _ => null
        };
        return security is not null && label is not null ? security.ChangeAsync(label, "Directory authentication") : Task.CompletedTask;
    }

    public Task<DirectoryNotificationPage> UserActivityNotificationsAsync(ClaimsPrincipal principal, int source, Guid guid, int page, int size, CancellationToken ct) =>
        activity.NotificationsAsync(principal, source, guid, page, size, ct);

    public Task<ServiceResult<EntraSettingsDto>> GetEntraAsync() =>
        ExecuteAsync(() => entra.GetAsync());

    public Task<ServiceResult<bool>> SaveEntraAsync(EntraSettingsDto input) =>
        ExecuteAsync(() => entra.SaveAsync(input));

    public Task<ServiceResult<bool>> EntraGenerateAsync() =>
        ExecuteAsync(() => entra.GenerateAsync());

    public Task<ServiceResult<bool>> EntraActivateAsync(bool testGraph) =>
        ExecuteAsync(() => entra.ActivateAsync(testGraph));

    public Task<ServiceResult<bool>> EntraDiscardPendingAsync() =>
        ExecuteAsync(() => entra.DiscardPendingAsync());

    public Task<ServiceResult<bool>> TestEntraAsync() =>
        ExecuteAsync(() => entra.TestAsync());
}
