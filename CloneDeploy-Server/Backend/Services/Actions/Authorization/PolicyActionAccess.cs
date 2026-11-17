using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

internal static class PolicyActionAccess
{
    internal static async Task<ClaimsPrincipal?> UserAsync(AuthenticationStateProvider authentication,
        IAuthorizationService authorization, string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true &&
            int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }
}
