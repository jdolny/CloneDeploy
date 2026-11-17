using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

// Single UI entry point for users, preferences, permissions, and user groups.
/// <summary>
/// Lets users manage accounts, user groups, permissions, and personal preferences.
/// </summary>
public sealed class AppUserService(
    AppUserAdministration users,
    AppUserPermissionService permissions,
    AppUserGroupService groups,
    ILogger<AppUserService> logger,
    AccountPasswordService passwords,
    LoginOptionsQuery loginOptions, SecurityActivity? security = null)
{
    public Task<ServiceResult<List<AppUser>>> GetAsync(string? searchText = null) => ExecuteAsync(async () => ServiceResult<List<AppUser>>.Ok(await users.GetAsync(searchText)));
    public Task<ServiceResult<bool>> AnyUsersAsync() => ExecuteAsync(async () => ServiceResult<bool>.Ok(await users.AnyUsersAsync()));
    public Task<ServiceResult<AppUser>> GetByIdAsync(int id) => ExecuteAsync(() => users.GetByIdAsync(id));
    public Task<ServiceResult<AppUser>> CreateAsync(AppUser user, string password) => ExecuteAsync(() => users.CreateAsync(user, password));
    public Task<ServiceResult<AppUser>> UpdateAsync(AppUser user, string? password) => ExecuteAsync(() => users.UpdateAsync(user, password), $"User #{user.Id}: membership {user.Membership}; web MFA {user.EnableMfa}; imaging MFA {user.EnableImagingMfa}");
    public Task<ServiceResult> DeleteAsync(int id) => ExecuteAsync(() => users.DeleteAsync(id), $"User #{id}");
    public Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids) => ExecuteAsync(async () =>
    {
        foreach (var id in ids.Distinct())
        {
            var result = await DeleteAsync(id);
            if (!result.Success) return result;
        }
        return ServiceResult.Ok();
    });
    public Task<ServiceResult<AppUserOptions>> GetOptionsAsync(int userId) => ExecuteAsync(() => users.GetOptionsAsync(userId));
    public Task<ServiceResult> UpdateColorModeAsync(int userId, ColorMode colorMode) => ExecuteAsync(() => users.UpdateColorModeAsync(userId, colorMode));
    public Task<ServiceResult> UpdateOptionsAsync(int userId, AppUserOptions options) => ExecuteAsync(() => users.UpdateOptionsAsync(userId, options));
    public Task<ServiceResult<List<AuditLog>>> GetAuditLogsAsync(int userId) => ExecuteAsync(async () => ServiceResult<List<AuditLog>>.Ok(await users.GetAuditLogsAsync(userId)));

    public Task<ServiceResult<PermissionsDto>> GetPermissionsAsync(int userId) => ExecuteAsync(() => permissions.GetAsync(userId));
    public Task<ServiceResult> SavePermissionsAsync(int userId, PermissionsDto input) => ExecuteAsync(() => permissions.SaveAsync(userId, input), $"User #{userId}");
    public Task<ServiceResult<PermissionsDto>> GetEffectivePermissionsAsync(int userId) => ExecuteAsync(() => permissions.GetEffectivePermissionsAsync(userId));

    public Task<ServiceResult<List<AppUserGroup>>> GetGroupsAsync(string? search = null) => ExecuteAsync(() => groups.GetAsync(search));
    public Task<ServiceResult<AppUserGroup>> GetGroupByIdAsync(int id) => ExecuteAsync(() => groups.GetByIdAsync(id));
    public Task<ServiceResult<AppUserGroup>> CreateGroupAsync(AppUserGroup input) => ExecuteAsync(() => groups.CreateAsync(input));
    public Task<ServiceResult> UpdateGroupGeneralAsync(AppUserGroup input) => ExecuteAsync(() => groups.UpdateGeneralAsync(input), $"Group #{input.Id}: {input.Name}");
    public Task<ServiceResult> DeleteGroupsAsync(IEnumerable<int> ids) => ExecuteAsync(() => groups.DeleteAsync(ids));
    public Task<ServiceResult<PermissionsDto>> GetGroupPermissionsAsync(int id) => ExecuteAsync(() => groups.GetPermissionsAsync(id));
    public Task<ServiceResult> SaveGroupPermissionsAsync(int id, PermissionsDto input) => ExecuteAsync(() => groups.SavePermissionsAsync(id, input), $"Group #{id}");
    public Task<ServiceResult<List<UserGroupMemberDto>>> GetGroupMembersAsync(int id) => ExecuteAsync(() => groups.GetMembersAsync(id));
    public Task<ServiceResult<List<UserGroupMemberDto>>> SearchUsersAsync(string? search, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => groups.SearchUsersAsync(search, cancellationToken));
    public Task<ServiceResult> SaveGroupMembersAsync(int id, IEnumerable<int> userIds) => ExecuteAsync(() => groups.SaveMembersAsync(id, userIds), $"Group #{id}");

    private async Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation, string target = "Application accounts/groups",
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "User.OperationFailed", null, operationName);
        await RecordChangeAsync(result, operationName, target);
        return result;
    }

    private async Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation, string target = "Application accounts/groups",
        [CallerMemberName] string operationName = "")
    {
        var result = await ServiceOperations.RunAsync(operation, logger, "User.OperationFailed", null, operationName);
        if (result.Data is AppUser account && operationName == nameof(CreateAsync)) target = $"User #{account.Id}: {account.UserName}; membership {account.Membership}";
        if (result.Data is AppUser updated && operationName == nameof(UpdateAsync))
            target = $"User #{updated.Id}: membership {updated.Membership}; web MFA {updated.EnableMfa}; imaging MFA {updated.EnableImagingMfa}";
        if (result.Data is AppUserGroup group) target = $"Group #{group.Id}: {group.Name}";
        await RecordChangeAsync(result, operationName, target);
        return result;
    }

    private Task RecordChangeAsync(ServiceResult result, string operation, string target)
    {
        var label = operation switch
        {
            nameof(CreateAsync) => "Account created", nameof(UpdateAsync) => "Account access/MFA settings saved",
            nameof(DeleteAsync) => "Account deleted",
            nameof(SavePermissionsAsync) => "User permissions changed", nameof(CreateGroupAsync) => "Application group created",
            nameof(UpdateGroupGeneralAsync) => "Group access/MFA settings saved", nameof(DeleteGroupsAsync) => "Application groups deleted",
            nameof(SaveGroupPermissionsAsync) => "Group permissions changed", nameof(SaveGroupMembersAsync) => "Group membership changed",
            nameof(SaveDirectoryMappingsAsync) => "Directory access mappings changed", nameof(ChangePasswordAsync) => "Account password changed",
            _ => null
        };
        var incomplete = result.Error?.Code is ErrorKeys.User.CreateIncomplete or ErrorKeys.User.UpdateIncomplete;
        return security is not null && label is not null && (result.Success || incomplete)
            ? security.ChangeAsync(label + (incomplete ? " (partially completed)" : ""), target) : Task.CompletedTask;
    }

    public Task<ServiceResult<AccountPasswordOptions>> GetPasswordAsync() =>
        ExecuteAsync(() => passwords.GetAsync());

    public Task<ServiceResult> ChangePasswordAsync(string? currentPassword, string newPassword, string confirmation) =>
        ExecuteAsync(() => passwords.ChangeAsync(currentPassword, newPassword, confirmation));

    public Task<ServiceResult<List<AppUserGroupDirectoryMapping>>> GetDirectoryMappingsAsync(int id) =>
        ExecuteAsync(() => groups.GetDirectoryMappingsAsync(id));

    public Task<ServiceResult<List<AppUserGroupDirectoryMapping>>> SearchDirectoryGroupsAsync(string? search, CancellationToken ct = default) =>
        ExecuteAsync(() => groups.SearchDirectoryGroupsAsync(search, ct));

    public Task<ServiceResult> SaveDirectoryMappingsAsync(int id, IEnumerable<AppUserGroupDirectoryMapping> mappings) =>
        ExecuteAsync(() => groups.SaveDirectoryMappingsAsync(id, mappings), $"Group #{id}");

    public Task<ServiceResult<bool>> IsEntraEnabledAsync() =>
        ExecuteAsync(() => loginOptions.IsEntraEnabledAsync());
}
