using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Saves user permissions and determines access granted through group membership.
/// </summary>
public class AppUserPermissionService(IDbContextFactory<CloneDeployDbContext> factory)
{
    public async Task<bool> RequiresImagingMfaAsync(AppUser user)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await MfaPolicy.RequiresImagingAsync(db, user);
    }

    public async Task<DirectoryApplicationAccessResult> GetDirectoryAccessAsync(int userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await DirectoryApplicationAccess.ForUserAsync(db, userId);
    }

    public async Task<ServiceResult<PermissionsDto>> GetAsync(int userId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var user = await db.Users
            .AsNoTracking()
            .Include(x => x.AppUserRights)
            .SingleOrDefaultAsync(x => x.Id == userId);

        if (user is null)
            return ServiceResult<PermissionsDto>.Fail(ErrorKeys.Common.NotFound, userId);

        var permissions = new PermissionsDto
        {
            Selected = user.AppUserRights
                .Select(x => x.Right)
                .ToHashSet(StringComparer.Ordinal)
        };

        return ServiceResult<PermissionsDto>.Ok(permissions);
    }

    public async Task<ServiceResult> SaveAsync(int userId, PermissionsDto permissions)
    {
        await using var db = await factory.CreateDbContextAsync();

        var user = await db.Users
            .Include(x => x.AppUserRights)
            .SingleOrDefaultAsync(x => x.Id == userId);

        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, userId);

        var validPermissions = Constants.Permissions.All
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        var selected = permissions.Selected
            .Where(validPermissions.Contains)
            .ToHashSet(StringComparer.Ordinal);

        var existing = user.AppUserRights
            .Where(x => validPermissions.Contains(x.Right))
            .ToList();

        db.AppUserRights.RemoveRange(existing);

        foreach (var permission in selected)
        {
            user.AppUserRights.Add(new AppUserRight
            {
                Right = permission
            });
        }

        await db.SaveChangesAsync();

        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<PermissionsDto>> GetEffectivePermissionsAsync(int userId)
    {
        var result = await GetAsync(userId);
        if (!result.HasData)
            return result;

        await using var db = await factory.CreateDbContextAsync();
        var access = await DirectoryApplicationAccess.ForUserAsync(db, userId);
        if (access.Managed && !access.Allowed) return ServiceResult<PermissionsDto>.Ok(new PermissionsDto());
        var groups = db.AppUserGroupMemberships.Where(x => x.AppUserId == userId).Select(x => x.AppUserGroupId);
        var inherited = await db.AppUserGroupRights.Where(x => (groups.Contains(x.AppUserGroupId) || access.GroupIds.Contains(x.AppUserGroupId)))
            .Select(x => x.Right).Distinct().ToListAsync();
        var valid = Constants.Permissions.All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        result.Data.Selected.UnionWith(inherited.Where(valid.Contains));
        return result;
    }
}
