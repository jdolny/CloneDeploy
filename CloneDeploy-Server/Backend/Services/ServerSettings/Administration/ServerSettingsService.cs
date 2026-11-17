using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ServerSettingsService(ICloneDeployDbFactory factory, ServerUrlSettingsStore urls,
    AuthenticationStateProvider authentication, IAuthorizationService authorization, ILogger<ServerSettingsService> logger)
{
    public async Task<ServiceResult<string?>> ReadMainServerAddressAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<string?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () => ServiceResult<string?>.Ok(await urls.ReadMainServerAddressAsync()), logger);
    }

    public async Task<ServiceResult<string?>> ReadAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<string?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () => ServiceResult<string?>.Ok(await urls.ReadAsync()), logger);
    }

    public async Task<ServiceResult<string>> SaveAsync(string url, string? expectedUrl)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<string>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            string normalized;
            try { normalized = urls.NormalizeManagementUrl(url) ?? throw new InvalidDataException("Enter the public application HTTPS URL."); }
            catch (InvalidDataException ex) { return ServiceResult<string>.Fail("ServerSettings.Validation", ex.Message); }
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Serialize first insert as well as updates; reject a stale administrator form.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
            var current = await urls.ReadAsync();
            if (current != expectedUrl) return ServiceResult<string>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.ServerSettingsService.ThePublicURLChangedSinceThisPageLoadedRefresh));
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == ServerUrlSettingsStore.PublicUrlKey);
            if (row is null) db.AppSettings.Add(new AppSetting { Name = ServerUrlSettingsStore.PublicUrlKey, Value = normalized });
            else row.Value = normalized;
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Public application URL",
                ObjectJson = JsonSerializer.Serialize(new { PreviousUrl = current, PublicUrl = normalized })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult<string>.Ok(normalized);
        }, logger);
    }

    private async Task<ClaimsPrincipal?> AdministratorAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
               (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ? user : null;
    }

    public async Task<ServiceResult<string?>> ReadBootOverrideAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<string?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () => ServiceResult<string?>.Ok(await urls.ReadBootOverrideAsync()), logger);
    }

    public async Task<ServiceResult<string?>> ReadBootUrlAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<string?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () => ServiceResult<string?>.Ok(await urls.ReadBootUrlAsync()), logger);
    }

    public async Task<ServiceResult<string?>> SaveBootOverrideAsync(string? url, string? expectedUrl)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<string?>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            string? normalized;
            try { normalized = ServerUrlSettingsStore.NormalizeBootUrl(url); }
            catch (InvalidDataException ex) { return ServiceResult<string?>.Fail("ServerSettings.Validation", ex.Message); }
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
            var current = await urls.ReadBootOverrideAsync();
            if (current != expectedUrl) return ServiceResult<string?>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.ServerSettingsService.ThePXEBootURLChangedSinceThisPageLoaded));
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == ServerUrlSettingsStore.BootUrlKey);
            // Empty explicitly clears a deployment override and restores the automatic HTTP URL.
            if (row is null) db.AppSettings.Add(new AppSetting { Name = ServerUrlSettingsStore.BootUrlKey, Value = normalized ?? "" });
            else row.Value = normalized ?? "";
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
                AuditType = AuditType.Update, ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "PXE boot URL",
                ObjectJson = JsonSerializer.Serialize(new { PreviousUrl = current, BootUrl = normalized })
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ServiceResult<string?>.Ok(normalized);
        }, logger);
    }
}
