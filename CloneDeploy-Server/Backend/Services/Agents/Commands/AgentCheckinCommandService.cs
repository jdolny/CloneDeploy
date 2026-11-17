using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Asks a connected device to check in immediately.
/// </summary>
public sealed class AgentCheckinCommandService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, TimeProvider clock,
    ILogger<AgentCheckinCommandService> logger)
{
    public async Task<AgentSendResultDto> RequestAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceCheckIn)).Succeeded
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException("Request Check-in permission is required.");
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) return new(false, "The agent is offline. Check-in was not requested.");
        if (connection.ServerInstanceId != server.Id)
            return new(false, "The agent is connected to another server instance. Request check-in from that instance.");

        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == deviceId, ct);
        var request = new AgentCheckinCommand(Guid.NewGuid(), device.Guid, clock.GetUtcNow().AddSeconds(30));
        var audit = new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Update,
            ObjectType = "DeviceCheckIn", ObjectId = deviceId, ObjectName = device.Name, LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
            ObjectJson = JsonSerializer.Serialize(new { request.RequestId, deviceId, Status = "Requested" })
        };
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
        AgentSendResultDto result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var receipt = await hub.Clients.Client(connection.ConnectionId)
                .InvokeAsync<AgentCheckinReceipt>(AgentHubProtocol.CheckInNow, request, timeout.Token);
            result = receipt is not null && receipt.RequestId == request.RequestId && receipt.Accepted
                ? new(true, "The agent accepted the check-in request. It will check in after any current policy work finishes.")
                : new(false, "The agent did not accept the check-in request.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            logger.LogWarning("Check-in request for device {DeviceId} was not confirmed ({ErrorType})", deviceId, ex.GetType().Name);
            result = new(false, "The agent did not confirm the request. It may be disconnected or need an update.");
        }
        audit.ObjectJson = JsonSerializer.Serialize(new { request.RequestId, deviceId, result.Acknowledged, result.Detail });
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.SaveChangesAsync(cleanup.Token);
        return result;
    }
}
