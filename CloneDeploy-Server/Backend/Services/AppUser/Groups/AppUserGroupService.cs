using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages user groups, their members, and their permissions.
/// </summary>
public class AppUserGroupService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<AppUserGroup>>> GetAsync(string? search = null)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<AppUserGroup>>.Fail(LocKeys.UserGroup.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var query = db.AppUserGroups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{search.Trim()}%"));
        return ServiceResult<List<AppUserGroup>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<AppUserGroup>> GetByIdAsync(int id)
    {
        if (!await IsAdminAsync())
            return ServiceResult<AppUserGroup>.Fail(LocKeys.UserGroup.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var group = await db.AppUserGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        return group is null ? ServiceResult<AppUserGroup>.Fail(ErrorKeys.Common.NotFound, id) : ServiceResult<AppUserGroup>.Ok(group);
    }

    public async Task<ServiceResult<AppUserGroup>> CreateAsync(AppUserGroup input)
    {
        if (!await IsAdminAsync())
            return ServiceResult<AppUserGroup>.Fail(LocKeys.UserGroup.Forbidden);

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult<AppUserGroup>.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.EnterAGroupNameOfAtMost200Characters));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('app-user-groups', 0))");
        if (await db.AppUserGroups.AnyAsync(x => x.Name.ToLower() == name.ToLower()))
            return ServiceResult<AppUserGroup>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var group = new AppUserGroup { Name = name, GrantAdministrator = input.GrantAdministrator,
            RequireWebMfa = input.RequireWebMfa || input.GrantAdministrator, RequireImagingMfa = input.RequireImagingMfa };
        db.AppUserGroups.Add(group);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult<AppUserGroup>.Ok(group);
    }

    public async Task<ServiceResult> UpdateGeneralAsync(AppUserGroup input)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(LocKeys.UserGroup.Forbidden);

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 200)
            return ServiceResult.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.EnterAGroupNameOfAtMost200Characters));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('app-user-groups', 0))");
        var group = await db.AppUserGroups.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (group is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, input.Id);

        if (await db.AppUserGroups.AnyAsync(x => x.Id != input.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail(ErrorKeys.Common.AlreadyExists, name);

        group.Name = name;
        group.GrantAdministrator = input.GrantAdministrator;
        group.RequireWebMfa = input.RequireWebMfa || input.GrantAdministrator;
        group.RequireImagingMfa = input.RequireImagingMfa;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        input.Name = name;
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(IEnumerable<int> groupIds)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(LocKeys.UserGroup.Forbidden);

        var ids = groupIds.Distinct().ToArray();
        await using var db = await factory.CreateContextAsync();
        var groups = await db.AppUserGroups.Where(x => ids.Contains(x.Id)).ToListAsync();
        if (groups.Count != ids.Length)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound);

        db.AppUserGroups.RemoveRange(groups);
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<PermissionsDto>> GetPermissionsAsync(int id)
    {
        if (!await IsAdminAsync())
            return ServiceResult<PermissionsDto>.Fail(LocKeys.UserGroup.Forbidden);

        await using var db = await factory.CreateContextAsync();
        if (!await db.AppUserGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<PermissionsDto>.Fail(ErrorKeys.Common.NotFound, id);

        return ServiceResult<PermissionsDto>.Ok(new PermissionsDto
        {
            Selected = (await db.AppUserGroupRights.Where(x => x.AppUserGroupId == id).Select(x => x.Right).ToListAsync()).ToHashSet(StringComparer.Ordinal)
        });
    }

    public async Task<ServiceResult> SavePermissionsAsync(int id, PermissionsDto permissions)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(LocKeys.UserGroup.Forbidden);

        var valid = Constants.Permissions.All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (permissions.Selected.Any(x => !valid.Contains(x)))
            return ServiceResult.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.OneOrMorePermissionsAreInvalid));

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"app-user-group:" + id}, 0))");
        if (!await db.AppUserGroups.AnyAsync(x => x.Id == id))
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);

        db.AppUserGroupRights.RemoveRange(await db.AppUserGroupRights.Where(x => x.AppUserGroupId == id).ToListAsync());
        db.AppUserGroupRights.AddRange(permissions.Selected.Select(right => new AppUserGroupRight { AppUserGroupId = id, Right = right }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<UserGroupMemberDto>>> GetMembersAsync(int id)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<UserGroupMemberDto>>.Fail(LocKeys.UserGroup.Forbidden);

        await using var db = await factory.CreateContextAsync();
        if (!await db.AppUserGroups.AnyAsync(x => x.Id == id))
            return ServiceResult<List<UserGroupMemberDto>>.Fail(ErrorKeys.Common.NotFound, id);

        var ids = db.AppUserGroupMemberships.Where(x => x.AppUserGroupId == id).Select(x => x.AppUserId);
        return ServiceResult<List<UserGroupMemberDto>>.Ok(await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.UserName).Select(x => new UserGroupMemberDto(x.Id, x.UserName!, x.Email)).ToListAsync());
    }

    public async Task<ServiceResult<List<UserGroupMemberDto>>> SearchUsersAsync(string? search, CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<UserGroupMemberDto>>.Fail(LocKeys.UserGroup.Forbidden);

        await using var db = await factory.CreateContextAsync();
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.UserName!, pattern) || (x.Email != null && EF.Functions.ILike(x.Email, pattern)));
        }
        return ServiceResult<List<UserGroupMemberDto>>.Ok(await query.OrderBy(x => x.UserName).ThenBy(x => x.Id).Take(100)
            .Select(x => new UserGroupMemberDto(x.Id, x.UserName!, x.Email)).ToListAsync(cancellationToken));
    }

    public async Task<ServiceResult> SaveMembersAsync(int id, IEnumerable<int> userIds)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(LocKeys.UserGroup.Forbidden);

        var ids = userIds.Distinct().ToArray();
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"app-user-group:" + id}, 0))");
        if (!await db.AppUserGroups.AnyAsync(x => x.Id == id))
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);

        if (await db.Users.CountAsync(x => ids.Contains(x.Id)) != ids.Length)
            return ServiceResult.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.OneOrMoreUsersNoLongerExistReloadThe));

        db.AppUserGroupMemberships.RemoveRange(await db.AppUserGroupMemberships.Where(x => x.AppUserGroupId == id).ToListAsync());
        db.AppUserGroupMemberships.AddRange(ids.Select(userId => new AppUserGroupMembership { AppUserGroupId = id, AppUserId = userId }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<AppUserGroupDirectoryMapping>>> GetDirectoryMappingsAsync(int id)
    {
        if (!await IsAdminAsync()) return ServiceResult<List<AppUserGroupDirectoryMapping>>.Fail(LocKeys.UserGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<AppUserGroupDirectoryMapping>>.Ok(await db.Set<AppUserGroupDirectoryMapping>().AsNoTracking().Where(x => x.AppUserGroupId == id).ToListAsync());
    }

    public async Task<ServiceResult<List<AppUserGroupDirectoryMapping>>> SearchDirectoryGroupsAsync(string? search, CancellationToken ct = default)
    {
        if (!await IsAdminAsync()) return ServiceResult<List<AppUserGroupDirectoryMapping>>.Fail(LocKeys.UserGroup.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var query = db.DirectoryObjects.AsNoTracking().Where(x => x.Kind == Backend.Enum.DirectoryObjectKind.Group &&
            !x.IsMissing && !x.Disabled && x.DirectorySource.IsEnabled);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.ToLower().Contains(search.ToLower()));
        return ServiceResult<List<AppUserGroupDirectoryMapping>>.Ok(await query.OrderBy(x => x.Name).Take(100).Select(x =>
            new AppUserGroupDirectoryMapping { DirectorySourceId = x.DirectorySourceId, ObjectGuid = x.ObjectGuid,
                DisplayName = x.Name + " — " + x.DirectorySource.Name }).ToListAsync(ct));
    }

    public async Task<ServiceResult> SaveDirectoryMappingsAsync(int id, IEnumerable<AppUserGroupDirectoryMapping> mappings)
    {
        if (!await IsAdminAsync()) return ServiceResult.Fail(LocKeys.UserGroup.Forbidden);
        var selected = mappings.DistinctBy(x => (x.DirectorySourceId, x.ObjectGuid)).ToList();
        if (selected.Count > 100) return ServiceResult.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.SelectAtMost100DirectoryGroups));
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await EntraAdministration.LockAsync(db);
        if (!await db.AppUserGroups.AnyAsync(x => x.Id == id)) return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);
        var old = await db.Set<AppUserGroupDirectoryMapping>().Where(x => x.AppUserGroupId == id).ToListAsync();
        foreach (var item in selected)
        {
            var group = await db.DirectoryObjects.Include(x => x.DirectorySource).SingleOrDefaultAsync(x => x.DirectorySourceId == item.DirectorySourceId &&
                x.ObjectGuid == item.ObjectGuid && x.Kind == Backend.Enum.DirectoryObjectKind.Group && !x.IsMissing && !x.Disabled && x.DirectorySource.IsEnabled);
            if (group is null && !old.Any(x => x.DirectorySourceId == item.DirectorySourceId && x.ObjectGuid == item.ObjectGuid))
                return ServiceResult.Fail(LocKeys.UserGroup.Validation, UiText.Get(LocKeys.Ui.AppUserGroupService.ASelectedDirectoryGroupIsUnavailableSynchronizeTheDirectory));
            item.AppUserGroupId = id;
            if (group is not null) item.DisplayName = group.Name + " — " + group.DirectorySource.Name;
        }
        db.RemoveRange(old);
        await db.SaveChangesAsync();
        db.AddRange(selected.Select(x => new AppUserGroupDirectoryMapping { AppUserGroupId = id, DirectorySourceId = x.DirectorySourceId,
            ObjectGuid = x.ObjectGuid, DisplayName = x.DisplayName }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    private async Task<bool> IsAdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }
}
