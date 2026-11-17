using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectoryNotificationRow(DeviceNotificationReceipt Receipt, string DeviceName);
public sealed record DirectoryNotificationPage(int Total, List<DirectoryNotificationRow> Rows);

internal sealed record DirectoryHistoryIdentity(string[] HelpKeys, string[] NotificationKeys, string? Sid)
{
    internal static async Task<DirectoryHistoryIdentity> ReadAsync(CloneDeployDbContext db, int source, Guid guid, CancellationToken ct)
    {
        // History remains available for disabled/missing users. Never match display names or emails.
        var user = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource).ThenInclude(x => x.SyncState)
            .SingleOrDefaultAsync(x => x.DirectorySourceId == source && x.ObjectGuid == guid && x.Kind == DirectoryObjectKind.User, ct)
            ?? throw new InvalidOperationException("This directory user is no longer available.");
        var keys = new List<string>();
        if (user.DirectorySource.Provider == DirectoryProvider.EntraId)
        {
            var tenant = user.DirectorySource.SyncState?.RootObjectGuid ?? await db.EntraConnections.Select(x => x.TenantId).SingleOrDefaultAsync(ct);
            if (tenant != Guid.Empty) keys.Add($"entra:{tenant:N}:{guid:N}");
        }
        else keys.Add($"ldap:{source}:{guid:N}");
        if (user.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true) keys.Add("sid:" + user.ObjectSid);
        var notices = new List<string> { $"directory:{source}:{guid:N}" };
        if (!string.IsNullOrWhiteSpace(user.ObjectSid)) notices.Add("directory:" + user.ObjectSid);
        return new(keys.ToArray(), notices.ToArray(), user.ObjectSid);
    }
}

public sealed class DirectoryUserActivityService(ICloneDeployDbFactory factory, IAuthorizationService authorization)
{
    public async Task<DirectoryNotificationPage> NotificationsAsync(ClaimsPrincipal principal, int source, Guid guid, int page, int size, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(principal, Constants.Permissions.AdminOnly)).Succeeded) throw new UnauthorizedAccessException();
        if (page < 0 || size is < 1 or > 100 || (long)page * size > int.MaxValue) throw new InvalidOperationException("Invalid history page.");
        await using var db = await factory.CreateContextAsync();
        var identity = await DirectoryHistoryIdentity.ReadAsync(db, source, guid, ct);
        var query = db.DeviceNotificationReceipts.AsNoTracking().Where(x => identity.NotificationKeys.Contains(x.UserKey) ||
            x.UserKey == "" && identity.Sid != null && identity.Sid != "" && x.UserSid == identity.Sid);
        var total = await query.CountAsync(ct);
        var rows = await (from receipt in query join device in db.Devices on receipt.DeviceId equals device.Id
            orderby receipt.DeliveredAtUtc descending, receipt.NotificationId, receipt.DeviceId, receipt.UserSid
            select new DirectoryNotificationRow(receipt, device.Name)).Skip(page * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(x => x.Receipt.NotificationId).Distinct().ToArray();
        var acknowledgments = await db.Set<UserNotificationAcknowledgment>().AsNoTracking()
            .Where(x => ids.Contains(x.NotificationId) && identity.NotificationKeys.Contains(x.UserKey)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var ack = acknowledgments.FirstOrDefault(x => x.NotificationId == row.Receipt.NotificationId && x.UserKey == row.Receipt.UserKey);
            if (row.Receipt.AcknowledgedAtUtc is null && ack is not null)
            {
                row.Receipt.AcknowledgedAtUtc = ack.AcknowledgedAtUtc;
                row.Receipt.AcknowledgedElsewhere = ack.AcknowledgedOnDeviceId != row.Receipt.DeviceId;
            }
        }
        return new(total, rows);
    }
}
