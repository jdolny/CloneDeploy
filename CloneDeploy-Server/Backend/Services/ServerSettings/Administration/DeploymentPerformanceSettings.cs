using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentPerformanceOptions(bool VerifyImage = true, int BlockMiB = 8, int CacheMiB = 64, int ParallelReads = 4)
{
    public const string Key = "Imaging.DeploymentPerformance";
    public bool Valid => BlockMiB is 4 or 8 or 16 && CacheMiB is >= 32 and <= 256 &&
        ParallelReads is >= 1 and <= 4 && CacheMiB >= BlockMiB * ParallelReads * 2 && CacheMiB % BlockMiB == 0;
    public static async Task<DeploymentPerformanceOptions> ReadAsync(CloneDeployDbContext db, CancellationToken ct = default)
    {
        var json = await db.AppSettings.Where(x => x.Name == Key).Select(x => x.Value).SingleOrDefaultAsync(ct);
        try { return json is not null && JsonSerializer.Deserialize<DeploymentPerformanceOptions>(json) is { Valid: true } value ? value : new(); }
        catch (JsonException) { return new(); }
    }
}

public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult<DeploymentPerformanceOptions>> ReadDeploymentPerformanceAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<DeploymentPerformanceOptions>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            return ServiceResult<DeploymentPerformanceOptions>.Ok(await DeploymentPerformanceOptions.ReadAsync(db));
        }, logger);
    }
    public async Task<ServiceResult<DeploymentPerformanceOptions>> SaveDeploymentPerformanceAsync(DeploymentPerformanceOptions value, DeploymentPerformanceOptions expected)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<DeploymentPerformanceOptions>.Fail("Pxe.Forbidden");
        if (!value.Valid) return ServiceResult<DeploymentPerformanceOptions>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.DeploymentPerformanceSettingsTab.Use48Or16MiBBlocks14));
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)");
            var previous = await DeploymentPerformanceOptions.ReadAsync(db);
            if (previous != expected) return ServiceResult<DeploymentPerformanceOptions>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.DeploymentPerformanceSettings.DeploymentSettingsChangedRefreshBeforeSaving));
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == DeploymentPerformanceOptions.Key);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = DeploymentPerformanceOptions.Key, Value = JsonSerializer.Serialize(value) });
            else row.Value = JsonSerializer.Serialize(value);
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Deployment performance",
                ObjectJson = JsonSerializer.Serialize(new { Previous = previous, Settings = value }) });
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return ServiceResult<DeploymentPerformanceOptions>.Ok(value);
        }, logger);
    }
}
