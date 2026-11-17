using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult<int>> ReadDeploymentLimitAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<int>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            return ServiceResult<int>.Ok(await ImagingDeploymentClaims.DeploymentLimitAsync(db));
        }, logger);
    }

    public async Task<ServiceResult<int>> SaveDeploymentLimitAsync(int limit, int expected)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<int>.Fail("Pxe.Forbidden");
        if (limit is < 0 or > 1000) return ServiceResult<int>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.ImagingQueueSettings.EnterADeploymentLimitBetween0And1000));
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947234, 0)");
            var previous = await ImagingDeploymentClaims.DeploymentLimitAsync(db);
            if (previous != expected) return ServiceResult<int>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.ImagingQueueSettings.TheDeploymentLimitChangedRefreshBeforeSaving));
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == ImagingDeploymentClaims.DeploymentLimitKey);
            var value = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = ImagingDeploymentClaims.DeploymentLimitKey, Value = value });
            else row.Value = value;
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update,
                ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Maximum concurrent deployments",
                ObjectJson = JsonSerializer.Serialize(new { Previous = previous, Limit = limit }) });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return ServiceResult<int>.Ok(limit);
        }, logger);
    }
}
