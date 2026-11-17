using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public static class MfaPolicy
{
    public static bool RequiresWeb(AppUser user) => user.EnableMfa || user.Membership == Constants.UserMembership.Admin;
    public static bool RequiresImaging(AppUser user) => user.EnableImagingMfa;

    public static bool IsEntraSession(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.HasClaim(ClaimTypes.AuthenticationMethod, "Entra");

    public static async Task<bool> RequiresWebAsync(CloneDeployDbContext db, AppUser user)
    {
        if (RequiresWeb(user)) return true;
        var access = await DirectoryApplicationAccess.ForUserAsync(db, user.Id);
        return access.Allowed && (access.Administrator || await db.AppUserGroups.AnyAsync(x => access.GroupIds.Contains(x.Id) && x.RequireWebMfa));
    }

    public static async Task<bool> RequiresImagingAsync(CloneDeployDbContext db, AppUser user)
    {
        if (RequiresImaging(user)) return true;
        var access = await DirectoryApplicationAccess.ForUserAsync(db, user.Id);
        return access.Allowed && await db.AppUserGroups.AnyAsync(x => access.GroupIds.Contains(x.Id) && x.RequireImagingMfa);
    }
}
