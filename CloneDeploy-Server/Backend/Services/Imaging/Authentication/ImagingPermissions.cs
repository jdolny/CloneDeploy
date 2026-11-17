using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class ImagingPermissions
{
    internal static async Task<ClaimsPrincipal?> BrowserAsync(AuthenticationStateProvider authentication,
        IAuthorizationService authorization, string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }

    // Imaging bearer and agent sessions deliberately do not carry browser claims.
    // Re-evaluate stored grants, directory access, and lockout for every operation.
    internal static async Task<bool> HasAsync(CloneDeployDbContext db, int userId, string permission, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null || user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow) return false;
        var access = await DirectoryApplicationAccess.ForUserAsync(db, userId, ct);
        if (access.Managed && !access.Allowed) return false;
        if (access.Allowed && access.Administrator) return true;
        if (permission == Constants.Permissions.AdminOnly) return false;
        if (await db.AppUserRights.AnyAsync(x => x.AppUserId == userId && x.Right == permission, ct)) return true;
        var groups = db.AppUserGroupMemberships.Where(x => x.AppUserId == userId).Select(x => x.AppUserGroupId);
        return await db.AppUserGroupRights.AnyAsync(x => x.Right == permission &&
            (groups.Contains(x.AppUserGroupId) || access.GroupIds.Contains(x.AppUserGroupId)), ct);
    }
}
