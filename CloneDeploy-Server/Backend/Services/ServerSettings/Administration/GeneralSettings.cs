using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ServerSettingsService
{
    public async Task<ServiceResult<string>> SaveGeneralAsync(string url, string? bootOverride, string? expectedUrl, string? expectedBootOverride, string? mainServerAddress, string? expectedMainServerAddress, ServerFeatures? features = null, ServerFeatures? expectedFeatures = null)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<string>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            string normalized;
            string? normalizedBoot;
            string? normalizedAddress;
            try
            {
                normalized = urls.NormalizeManagementUrl(url) ?? throw new InvalidDataException("Enter the public application HTTPS URL.");
                normalizedBoot = ServerUrlSettingsStore.NormalizeBootUrl(bootOverride);
                normalizedAddress = ServerUrlSettingsStore.NormalizeMainServerAddress(mainServerAddress);
            }
            catch (InvalidDataException ex) { return ServiceResult<string>.Fail("ServerSettings.Validation", ex.Message); }
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
            var current = await urls.ReadAsync();
            var currentBoot = await urls.ReadBootOverrideAsync();
            var currentAddress = await urls.ReadMainServerAddressAsync();
            if (current != expectedUrl || currentBoot != expectedBootOverride || currentAddress != expectedMainServerAddress)
                return ServiceResult<string>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.GeneralSettings.TheServerSettingsChangedSinceThisPageLoadedRefresh));
            if (features is not null)
            {
                var featureRow = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == ServerFeatureSettings.Key);
                var previous = featureRow?.Value is null ? new ServerFeatures() : JsonSerializer.Deserialize<ServerFeatures>(featureRow.Value);
                if (expectedFeatures is null || previous != expectedFeatures)
                    return ServiceResult<string>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.GeneralSettings.ServerFeaturesChangedRefreshBeforeSaving));
                if (featureRow is null) db.AppSettings.Add(new AppSetting { Name = ServerFeatureSettings.Key, Value = JsonSerializer.Serialize(features) });
                else featureRow.Value = JsonSerializer.Serialize(features);
                if (previous != features)
                    db.AuditLogs.Add(new AuditLog
                    {
                        AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                        AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Enabled server features",
                        ObjectJson = JsonSerializer.Serialize(new { Previous = previous, Settings = features })
                    });
            }
            foreach (var (key, value) in new[] { (ServerUrlSettingsStore.PublicUrlKey, normalized), (ServerUrlSettingsStore.BootUrlKey, normalizedBoot ?? ""), (ServerUrlSettingsStore.MainServerAddressKey, normalizedAddress ?? "") })
            {
                var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == key);
                if (row is null) db.AppSettings.Add(new AppSetting { Name = key, Value = value });
                else row.Value = value;
            }
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Server addresses",
                ObjectJson = JsonSerializer.Serialize(new { PreviousUrl = current, PublicUrl = normalized, PreviousBootUrl = currentBoot, BootUrl = normalizedBoot, PreviousMainServerAddress = currentAddress, MainServerAddress = normalizedAddress })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult<string>.Ok(normalized);
        }, logger);
    }
}
