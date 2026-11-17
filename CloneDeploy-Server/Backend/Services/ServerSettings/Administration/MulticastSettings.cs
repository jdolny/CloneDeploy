using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ServerSettingsService
{
    public const string MulticastSenderAddressKey = "Imaging.MulticastSenderAddress";

    public async Task<ServiceResult<string>> ReadMulticastSenderAddressAsync()
    {
        if (await AdministratorAsync() is null) return ServiceResult<string>.Fail("Pxe.Forbidden");
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            return ServiceResult<string>.Ok(await db.AppSettings.Where(x => x.Name == MulticastSenderAddressKey)
                .Select(x => x.Value).SingleOrDefaultAsync() ?? "");
        }, logger);
    }

    public async Task<ServiceResult<string>> SaveMulticastSenderAddressAsync(string? address, string expected)
    {
        var user = await AdministratorAsync();
        if (user is null) return ServiceResult<string>.Fail("Pxe.Forbidden");
        var value = address?.Trim() ?? "";
        if (value.Length > 0)
        {
            if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.GetAddressBytes()[0] is 0 or >= 224)
                return ServiceResult<string>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.MulticastSettings.EnterASpecificUnicastIPv4AddressForTheMain));
            value = ip.ToString();
        }
        return await ServiceOperations.RunAsync(async () =>
        {
            await using var db = await factory.CreateContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
            var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == MulticastSenderAddressKey);
            var previous = row?.Value ?? "";
            if (previous != expected)
                return ServiceResult<string>.Fail("ServerSettings.Validation", UiText.Get(LocKeys.Ui.MulticastSettings.TheMulticastSenderAddressChangedRefreshBeforeSaving));
            if (row is null) db.AppSettings.Add(new AppSetting { Name = MulticastSenderAddressKey, Value = value });
            else row.Value = value;
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
                UserName = user.Identity?.Name ?? "Administrator", AuditType = AuditType.Update,
                ObjectType = "ServerSettings", ObjectId = 0, ObjectName = "Multicast sender IPv4 address",
                ObjectJson = JsonSerializer.Serialize(new { Previous = previous, Address = value }) });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return ServiceResult<string>.Ok(value);
        }, logger);
    }
}
