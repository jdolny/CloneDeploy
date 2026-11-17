using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides connection checks and status information for storage locations.
/// </summary>
public sealed class StorageMountService(StorageMountCoordinator mounts, AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public string ProcessIdentity => mounts.ProcessIdentity;
    public string RootPath => mounts.RootPath;
    public async Task<ServiceResult<List<StorageMountStatusDto>>> CheckAsync(int providerId)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<List<StorageMountStatusDto>>.Fail("StorageMount.Forbidden");
        await mounts.CheckAsync(providerId);
        return ServiceResult<List<StorageMountStatusDto>>.Ok(mounts.GetStatuses());
    }

    public async Task<ServiceResult<List<StorageMountStatusDto>>> GetAsync(bool check = false)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<List<StorageMountStatusDto>>.Fail("StorageMount.Forbidden");
        if (check) await mounts.CheckAllAsync();
        return ServiceResult<List<StorageMountStatusDto>>.Ok(mounts.GetStatuses());
    }
}
