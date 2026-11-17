using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult<ServerFeatures>> ReadServerFeaturesAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<ServerFeatures>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
            ServiceResult<ServerFeatures>.Ok(await new ServerFeatureSettings(factory).ReadAsync()), logger);
    }

    public async Task<ServiceResult<ServerFeatures>> SaveServerFeaturesAsync(ServerFeatures value, ServerFeatures expected)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<ServerFeatures>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == ServerFeatureSettings.Key);
            var previous = row?.Value is null ? new ServerFeatures() : JsonSerializer.Deserialize<ServerFeatures>(row.Value);
            if (previous != expected)
                return ServiceResult<ServerFeatures>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.GeneralSettings.ServerFeaturesChangedRefreshBeforeSaving));
            if (row is null) db.AppSettings.Add(new AppSetting { Name = ServerFeatureSettings.Key, Value = JsonSerializer.Serialize(value) });
            else row.Value = JsonSerializer.Serialize(value);
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Enabled server features",
                ObjectJson = JsonSerializer.Serialize(new { Previous = previous, Settings = value })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult<ServerFeatures>.Ok(value);
        }, logger);
    }
}
