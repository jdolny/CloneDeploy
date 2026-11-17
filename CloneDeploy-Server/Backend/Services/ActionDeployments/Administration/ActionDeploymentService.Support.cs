using System.Runtime.CompilerServices;
using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks administrator access and reports problems with action deployment requests.
/// </summary>
public sealed partial class ActionDeploymentService
{
    private async Task<ClaimsPrincipal?> GetAdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _))
            return null;

        return user;
    }

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.Deployment.Failed, operationName: operationName);

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.Deployment.Failed, operationName: operationName);
}
