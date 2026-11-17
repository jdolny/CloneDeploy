using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SharedResourceAccess(AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public async Task<bool> AllowedAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && (await authorization.AuthorizeAsync(user, permission)).Succeeded;
    }
}
