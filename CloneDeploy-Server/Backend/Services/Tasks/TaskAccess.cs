using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed record TaskAccess(ClaimsPrincipal User, int UserId, bool ViewAll, bool ManageAll)
{
    public bool CanView(int? owner) => ViewAll || owner == UserId;
    public bool CanManage(int? owner) => ManageAll || owner == UserId;

    public static async Task<TaskAccess?> ReadAsync(AuthenticationStateProvider authentication, IAuthorizationService authorization)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id <= 0)
            return null;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.TaskOwnAccess)).Succeeded) return null;
        var manage = (await authorization.AuthorizeAsync(user, Constants.Permissions.TaskManageAll)).Succeeded;
        var view = manage || (await authorization.AuthorizeAsync(user, Constants.Permissions.TaskViewAll)).Succeeded;
        return new(user, id, view, manage);
    }
}
