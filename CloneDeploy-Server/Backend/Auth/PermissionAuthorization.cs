using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Auth;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public class PermissionHandler(AppUserPermissionService permissions) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return;
        // Group membership may have changed since the cookie was issued.
        var access = await permissions.GetDirectoryAccessAsync(userId);
        if (!access.Allowed) return;
        if (requirement.Permission == "Directory.ApplicationAccess" || access.Administrator || requirement.Permission != Constants.Permissions.AdminOnly &&
            Matches(requirement.Permission, (await permissions.GetEffectivePermissionsAsync(userId)).Data?.Selected ?? []))
            context.Succeed(requirement);
    }

    private static bool Matches(string permission, IEnumerable<string> grants) => permission == Constants.Permissions.HelpDeskAccess
        ? grants.Any(Constants.Permissions.HelpDeskViews.Contains)
        : permission == Constants.Permissions.DeviceRemoteView
            ? grants.Any(x => x == Constants.Permissions.DeviceRemoteView || x == Constants.Permissions.DeviceRemoteControl)
            : grants.Contains(permission);
}

public class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<int>> roleManager,
    IOptions<IdentityOptions> options,
    AppUserPermissionService permissionService)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<int>>(
        userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        var access = await permissionService.GetDirectoryAccessAsync(user.Id);
        foreach (var claim in identity.FindAll(nameof(AppUser.Membership)).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(nameof(AppUser.Membership), access.Allowed && access.Administrator ? Constants.UserMembership.Admin : Constants.UserMembership.User));
        var permissions = await permissionService.GetEffectivePermissionsAsync(user.Id);
        if (permissions.HasData)
        {
            foreach (var permission in permissions.Data.Selected)
                identity.AddClaim(new Claim("Permission", permission));
        }
        return identity;
    }
}
