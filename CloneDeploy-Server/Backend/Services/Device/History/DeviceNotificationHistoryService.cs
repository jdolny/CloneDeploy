using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class DeviceNotificationHistoryService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<DeviceNotificationHistoryService> logger)
{
    public Task<ServiceResult<List<DeviceNotificationReceipt>>> GetAsync(int deviceId) => ServiceOperations.RunAsync(async () =>
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<DeviceNotificationReceipt>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var rows = await db.DeviceNotificationReceipts.AsNoTracking().Where(x => x.DeviceId == deviceId &&
            (x.UserSid != "" || !db.DeviceNotificationReceipts.Any(r => r.DeviceId == deviceId && r.NotificationId == x.NotificationId && r.UserSid != "")))
            .OrderByDescending(x => x.DeliveredAtUtc).ThenBy(x => x.UserName).Take(200).ToListAsync();
        var ids = rows.Select(x => x.NotificationId).Distinct().ToArray();
        var keys = rows.Select(x => x.UserKey).Distinct().ToArray();
        var acknowledgments = await db.Set<UserNotificationAcknowledgment>().AsNoTracking()
            .Where(x => ids.Contains(x.NotificationId) && keys.Contains(x.UserKey)).ToDictionaryAsync(x => (MessageId: x.NotificationId, x.UserKey));
        foreach (var row in rows)
            if (row.AcknowledgedAtUtc is null && acknowledgments.TryGetValue((row.NotificationId, row.UserKey), out var acknowledgment))
            {
                row.AcknowledgedAtUtc = acknowledgment.AcknowledgedAtUtc;
                row.AcknowledgedElsewhere = acknowledgment.AcknowledgedOnDeviceId != deviceId;
            }
        return ServiceResult<List<DeviceNotificationReceipt>>.Ok(rows);
    }, logger, "Policy.Failed");
}
