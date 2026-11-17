using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class HelpDeskScopeSettings
{
    public Guid Revision { get; set; }
    public bool AllDevices { get; set; }
    public bool AllUsers { get; set; }
    public int[] DeviceGroupIds { get; set; } = [];
    public int[] UserAudienceIds { get; set; } = [];
}

public sealed class HelpDeskScope(ICloneDeployDbFactory factory, IAuthorizationService authorization)
{
    private const string Key = "HelpDesk.Scope";
    public const string Denied = "New help requests are not available for your account on this device. You can still read and reply to existing requests.";

    internal static async Task<HelpDeskScopeSettings> ReadAsync(CloneDeployDbContext db, CancellationToken ct) =>
        JsonSerializer.Deserialize<HelpDeskScopeSettings>(await db.AppSettings.Where(x => x.Name == Key)
            .Select(x => x.Value).SingleOrDefaultAsync(ct) ?? "{}") ?? new();

    internal static async Task<bool> CanCreateAsync(CloneDeployDbContext db, int deviceId, string sid, CancellationToken ct)
    {
        var scope = await ReadAsync(db, ct);
        if (!scope.AllDevices && !await DeviceGroupMembershipQuery.DeviceIds(db, scope.DeviceGroupIds).ContainsAsync(deviceId, ct)) return false;
        return scope.AllUsers || (await PolicyAudienceDelivery.MatchingAudiencesAsync(db, sid, scope.UserAudienceIds, ct)).Count > 0;
    }

    private async Task AdminAsync(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) throw new UnauthorizedAccessException();
    }

    public async Task<(HelpDeskScopeSettings Settings, DeviceGroup[] Groups, UserAudience[] Audiences)> LoadAsync(ClaimsPrincipal user)
    {
        await AdminAsync(user);
        await using var db = await factory.CreateContextAsync();
        return (await ReadAsync(db, default), await db.DeviceGroups.AsNoTracking().OrderBy(x => x.Name).ToArrayAsync(),
            await db.Set<UserAudience>().AsNoTracking().OrderBy(x => x.Name).ToArrayAsync());
    }

    public async Task SaveAsync(ClaimsPrincipal user, HelpDeskScopeSettings input)
    {
        await AdminAsync(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == Key);
        var previous = JsonSerializer.Deserialize<HelpDeskScopeSettings>(row?.Value ?? "{}") ?? new();
        if (previous.Revision != input.Revision) throw new InvalidOperationException("Help Desk scope changed. Reload before saving.");
        var groups = input.DeviceGroupIds.Distinct().ToArray();
        var audiences = input.UserAudienceIds.Distinct().ToArray();
        if (groups.Length > 500 || audiences.Length > 500 ||
            await db.DeviceGroups.CountAsync(x => groups.Contains(x.Id)) != groups.Length ||
            await db.Set<UserAudience>().CountAsync(x => audiences.Contains(x.Id)) != audiences.Length)
            throw new InvalidOperationException("A selected group or audience is unavailable. Reload the scope settings.");
        var saved = new HelpDeskScopeSettings { Revision = Guid.NewGuid(), AllDevices = input.AllDevices, AllUsers = input.AllUsers,
            DeviceGroupIds = groups, UserAudienceIds = audiences };
        if (row is null) db.AppSettings.Add(row = new AppSetting { Name = Key });
        row.Value = JsonSerializer.Serialize(saved);
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            UserName = user.Identity?.Name ?? "", AuditType = AuditType.Update, ObjectType = "HelpDeskScope",
            ObjectName = "Help Desk scope", LoggedAtUtc = DateTime.UtcNow, ObjectJson = row.Value });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        input.Revision = saved.Revision;
    }
}
