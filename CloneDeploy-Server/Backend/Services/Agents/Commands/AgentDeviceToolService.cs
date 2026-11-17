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

/// <summary>Authorizes live device tools and audits changes requested from the connected agent.</summary>
public sealed class AgentDeviceToolService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, TimeProvider clock)
{
    public async Task<AgentDeviceToolReply> ExecuteAsync(ClaimsPrincipal user, int deviceId, AgentDeviceOperation operation,
        int offset = 0, string? serviceName = null, int processId = 0, long processStartedUtcTicks = 0, CancellationToken ct = default)
    {
        var permission = operation switch
        {
            AgentDeviceOperation.Services or AgentDeviceOperation.StartService or AgentDeviceOperation.StopService or AgentDeviceOperation.RestartService => Constants.Permissions.DeviceServices,
            AgentDeviceOperation.Processes or AgentDeviceOperation.EndProcess => Constants.Permissions.DeviceProcesses,
            AgentDeviceOperation.Reboot => Constants.Permissions.DeviceReboot,
            AgentDeviceOperation.Shutdown => Constants.Permissions.DeviceShutdown,
            AgentDeviceOperation.RenameComputer => Constants.Permissions.DeviceEdit,
            AgentDeviceOperation.SystemStatus => Constants.Permissions.DeviceView,
            _ => throw new UnauthorizedAccessException("Unknown device operation.")
        };
        return await ExecuteAuthorizedAsync(user, deviceId, operation, offset, serviceName, processId, processStartedUtcTicks, ct, permission);
    }

    private async Task<AgentDeviceToolReply> ExecuteAuthorizedAsync(ClaimsPrincipal user, int deviceId, AgentDeviceOperation operation, int offset, string? serviceName, int processId, long processStartedUtcTicks, CancellationToken ct, string permission)
    {
        if (!(await authorization.AuthorizeAsync(user, permission)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException("You do not have permission to use this device tool.");
        var id = Guid.NewGuid();
        AgentDeviceToolReply Failure(string detail) => AgentDeviceToolReply.Result(id, false, detail);
        if (!System.Enum.IsDefined(operation) || offset < 0 || offset > 100000 || serviceName?.Length > 256)
            return Failure("Invalid device tool request.");
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) return Failure("The agent is offline.");
        if (connection.ServerInstanceId != server.Id) return Failure("The agent is connected to another server instance. Use that instance to connect.");
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct);
        if (device is null) return Failure("The device is unavailable.");
        var request = new AgentDeviceToolRequest(id, device.Guid, clock.GetUtcNow().AddSeconds(45), operation, offset,
            serviceName, processId, processStartedUtcTicks);
        AuditLog? audit = null;
        if (operation is not (AgentDeviceOperation.Services or AgentDeviceOperation.Processes or AgentDeviceOperation.SystemStatus))
        {
            audit = new AuditLog
            {
                AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = operation switch
                    { AgentDeviceOperation.Reboot => AuditType.Reboot, AgentDeviceOperation.Shutdown => AuditType.Shutdown, _ => AuditType.Update },
                ObjectType = "DeviceTools", ObjectId = deviceId, ObjectName = device.Name, LoggedAtUtc = clock.GetUtcNow().UtcDateTime,
                ObjectJson = JsonSerializer.Serialize(new { request, Status = "Requested" })
            };
            db.AuditLogs.Add(audit);
            await db.SaveChangesAsync(ct);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        AgentDeviceToolReply reply;
        try
        {
            reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentDeviceToolReply>(AgentHubProtocol.DeviceTools, request, timeout.Token);
            if (reply is null || reply.RequestId != id || reply.Detail is null || reply.Detail.Length > 512 ||
                reply.Services is null || reply.Processes is null || reply.Services.Length > AgentDeviceToolReply.PageSize ||
                reply.Processes.Length > AgentDeviceToolReply.PageSize ||
                reply.Services.Any(x => x is null || !Valid(x.Name) || !Valid(x.DisplayName) || !Valid(x.Status) || !Valid(x.StartupType)) ||
                reply.Processes.Any(x => x is null || !Valid(x.Name)) ||
                (operation == AgentDeviceOperation.SystemStatus && reply.Success && reply.SystemStatus is null) ||
                (reply.SystemStatus is { } status && !ValidStatus(status)))
                reply = Failure("The agent returned an invalid response.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            reply = Failure("The agent did not confirm the operation. Refresh to check its current state; the agent may need an update or reconnection.");
        }
        if (audit is not null)
        {
            audit.ObjectJson = JsonSerializer.Serialize(new { request, reply.Success, reply.Detail });
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await db.SaveChangesAsync(cleanup.Token);
        }
        return reply;
    }

    // Scheduler-only entry point. Public device tools retain their individual device permissions.
    internal Task<AgentDeviceToolReply> ShutdownGroupMemberAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct) =>
        ExecuteAuthorizedAsync(user, deviceId, AgentDeviceOperation.Shutdown, 0, null, 0, 0, ct,
            Constants.Permissions.DeviceGroupPowerManagement);

    private static bool Valid(string? text) => text is not null && text.Length <= 256;

    internal static bool ValidStatus(AgentSystemStatus status) => status.UptimeSeconds >= 0 && status.UptimeSeconds <= long.MaxValue / TimeSpan.TicksPerSecond
        && status.CollectedAtUtc != default && status.LoggedOnUsers is { Length: <= 1024 }
        && status.LoggedOnUsers.All(Valid) && (status.UsersError is null || Valid(status.UsersError));
}
