using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks imaging permissions and temporary sign-in lockouts.
/// </summary>
public sealed class ImagingAccess(UserManager<AppUser> users, AppUserPermissionService permissions)
{
    public Task<bool> RequiresMfaAsync(AppUser user) => permissions.RequiresImagingMfaAsync(user);
    public async Task<bool> Allowed(AppUser user)
    {
        if (await users.IsLockedOutAsync(user)) return false;
        var directory = await permissions.GetDirectoryAccessAsync(user.Id);
        if (directory.Managed && !directory.Allowed) return false;
        if (directory.Allowed && directory.Administrator) return true;
        var rights = await permissions.GetEffectivePermissionsAsync(user.Id);
        return rights.HasData && rights.Data.Selected.Contains(Constants.Permissions.ImagingAccess);
    }
    public async Task<string[]> GrantsAsync(AppUser user)
    {
        if (!await Allowed(user)) return [];
        var directory = await permissions.GetDirectoryAccessAsync(user.Id);
        if (directory.Allowed && directory.Administrator)
            return Constants.Permissions.All.Select(x => x.Key).ToArray();
        var rights = await permissions.GetEffectivePermissionsAsync(user.Id);
        return rights.HasData ? rights.Data.Selected.ToArray() : [];
    }

}
