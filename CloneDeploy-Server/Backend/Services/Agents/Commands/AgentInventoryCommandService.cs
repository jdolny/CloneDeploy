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
/// Asks a connected device to send an updated inventory.
/// </summary>
public sealed class AgentInventoryCommandService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, TimeProvider clock,
    ILogger<AgentInventoryCommandService> logger)
{
    public async Task<AgentSendResultDto> CollectAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceInventory)).Succeeded
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException("Collect Inventory permission is required.");
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) return new(false, "The agent is offline. Inventory was not requested.");
        if (connection.ServerInstanceId != server.Id)
            return new(false, "The agent is connected to another server instance. Request inventory from that instance.");

        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == deviceId, ct);
        var request = new AgentInventoryRequest(Guid.NewGuid(), device.Guid, clock.GetUtcNow().AddSeconds(30));
        var audit = new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Update,
            ObjectType = "DeviceInventory", ObjectId = deviceId, ObjectName = device.Name, LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
            ObjectJson = JsonSerializer.Serialize(new { request.RequestId, deviceId, Status = "Collecting" })
        };
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
        AgentSendResultDto result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            var receipt = await hub.Clients.Client(connection.ConnectionId)
                .InvokeAsync<AgentInventoryReceipt>(AgentHubProtocol.CollectInventory, request, timeout.Token);
            result = receipt is not null && receipt.RequestId == request.RequestId
                ? new(receipt.Saved, receipt.Detail) : new(false, "The agent returned an invalid inventory response.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            logger.LogWarning(ex, "Inventory {RequestId} for device {DeviceId} did not acknowledge completion", request.RequestId, deviceId);
            result = new(false, "Inventory completion was not confirmed. Check the device's last inventory time before retrying.");
        }
        audit.ObjectJson = JsonSerializer.Serialize(new { request.RequestId, deviceId, result.Acknowledged, result.Detail });
        await db.SaveChangesAsync(CancellationToken.None);
        return result;
    }
}
