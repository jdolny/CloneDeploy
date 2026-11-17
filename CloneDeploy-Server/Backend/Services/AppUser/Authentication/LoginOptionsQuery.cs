using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Public sign-in capabilities; never returns connection or credential details.</summary>
public sealed class LoginOptionsQuery(ICloneDeployDbFactory factory)
{
    public async Task<ServiceResult<bool>> IsEntraEnabledAsync()
    {
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<bool>.Ok(await db.Set<EntraConnection>()
            .AnyAsync(x => x.SsoEnabled && x.CertificateEncrypted != null));
    }
}
