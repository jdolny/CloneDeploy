using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Records device check-ins and tells agents when to check in again.
/// </summary>
public sealed class AgentCheckinService(ICloneDeployDbFactory factory, TimeProvider clock,
    DeviceTaskQueue queue, ILogger<AgentCheckinService> logger)
{
    public Task<ServiceResult<AgentCheckinResponseDto>> CheckInAsync(int deviceId, Guid deviceGuid,
        AgentCheckinRequestDto request, string? remoteIp, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => CheckInCoreAsync(deviceId, deviceGuid, request, remoteIp, ct), logger);

    private async Task<ServiceResult<AgentCheckinResponseDto>> CheckInCoreAsync(int deviceId, Guid deviceGuid,
        AgentCheckinRequestDto request, string? remoteIp, CancellationToken ct)
    {
        if (!CloneDeploy_Agent.Contracts.AgentPlatform.Supported(request.Platform, request.Architecture))
            return ServiceResult<AgentCheckinResponseDto>.Fail(ServiceErrorCodes.Invalid, UiText.Get(LocKeys.Ui.AgentCheckinService.UnsupportedAgentPlatformOrArchitecture));
        if (request.UserLogins is { } logins && (logins.Count > 1000 || logins.Any(x => x is null
            || x.ClientDatabaseLoginId <= 0 || string.IsNullOrWhiteSpace(x.UserName) || x.UserName.Length > 512
            || x.LoggedInAtUtc == default || x.LoggedInAtUtc.Kind != DateTimeKind.Utc
            || (x.LoggedOutAtUtc is { } logout && (logout.Kind != DateTimeKind.Utc || logout < x.LoggedInAtUtc)))))
            return ServiceResult<AgentCheckinResponseDto>.Fail(ServiceErrorCodes.Invalid, UiText.Get(LocKeys.Ui.AgentCheckinService.InvalidUserLoginHistory));
        var computerName = request.ComputerName?.Trim();
        if (request.ComputerName is not null && (string.IsNullOrWhiteSpace(computerName)
            || request.ComputerName.Length > 128 || request.ComputerName.Any(char.IsControl)))
            return ServiceResult<AgentCheckinResponseDto>.Fail(ServiceErrorCodes.Invalid, "Invalid computer name.");
        var lease = await queue.TryBeginCheckinAsync(deviceGuid, ct);
        if (lease is null)
        {
            return ServiceResult<AgentCheckinResponseDto>.Fail(ServiceErrorCodes.Busy, UiText.Get(LocKeys.Ui.AgentCheckinService.CheckInCapacityIsBusyRetryShortly));
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining = lease.ExpiresAtUtc - clock.GetUtcNow().UtcDateTime;
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            var requestToken = deadline.Token;
            var now = clock.GetUtcNow().UtcDateTime;
            var address = remoteIp;
            await using var db = await factory.CreateContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(requestToken);
            var updated = await db.Devices.Where(x => x.Id == deviceId && x.Guid == deviceGuid && x.ArchivedAtUtc == null && x.ProvisionStatus == DeviceProvisionStatus.Provisioned)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, x => computerName ?? x.Name)
                    .SetProperty(x => x.LastCheckinAtUtc, now)
                    .SetProperty(x => x.AgentPlatform, request.Platform).SetProperty(x => x.ClientVersion, request.ClientVersion).SetProperty(x => x.LastKnownIp, address), requestToken);
            if (updated != 1) return ServiceResult<AgentCheckinResponseDto>.Fail(ServiceErrorCodes.Forbidden, UiText.Get(LocKeys.Ui.AgentCheckinService.DeviceIsUnavailable));
            if (request.UserLogins is { Count: > 0 } history)
            {
                var ids = history.Select(x => x.ClientDatabaseLoginId).Distinct().ToArray();
                var existing = await db.DeviceLogins.Where(x => x.DeviceId == deviceId
                    && x.ClientDatabaseLoginId.HasValue && ids.Contains(x.ClientDatabaseLoginId.Value)).ToListAsync(requestToken);
                foreach (var login in history)
                {
                    // PostgreSQL stores microseconds; normalize before matching a replayed .NET timestamp.
                    var loggedIn = new DateTime(login.LoggedInAtUtc.Ticks / 10 * 10, DateTimeKind.Utc);
                    // Include timestamp and username so a recreated client journal can reuse numeric IDs safely.
                    var row = existing.FirstOrDefault(x => x.ClientDatabaseLoginId == login.ClientDatabaseLoginId
                        && x.LoggedInAtUtc == loggedIn && x.UserName == login.UserName);
                    if (row is null)
                    {
                        row = new DeviceLogin { DeviceId = deviceId, ClientDatabaseLoginId = login.ClientDatabaseLoginId,
                            UserName = login.UserName, LoggedInAtUtc = loggedIn,
                            LoggedOutAtUtc = DateTime.SpecifyKind(default, DateTimeKind.Utc) };
                        db.DeviceLogins.Add(row);
                        existing.Add(row);
                    }
                    // Replaying an earlier, open snapshot must never erase a recorded logout.
                    if (login.LoggedOutAtUtc is { } logout && logout > row.LoggedOutAtUtc)
                        row.LoggedOutAtUtc = new DateTime(logout.Ticks / 10 * 10, DateTimeKind.Utc);
                }
                await db.SaveChangesAsync(requestToken);
            }
            var settings = await DeviceTaskQueue.ReadLimitsAsync(db);
            // Deliver metadata changes only after this check-in commits, over the existing UI feed.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_notify('device_activity_changed', current_schema() || ':' || {deviceId}::text)", requestToken);
            await transaction.CommitAsync(requestToken);
            return ServiceResult<AgentCheckinResponseDto>.Ok(new AgentCheckinResponseDto(1, deviceGuid, now, settings.CheckinIntervalMinutes * 60, true));
        }
        finally
        {
            // Release even when the client disconnects or the request deadline expires.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await queue.EndCheckinAsync(deviceGuid, lease.Id, cleanup.Token);
        }
    }
}
