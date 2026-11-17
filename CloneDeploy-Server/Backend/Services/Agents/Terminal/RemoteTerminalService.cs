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

public sealed class RemoteTerminalService(ICloneDeployDbFactory factory, IAuthorizationService authorization,
    RemoteTerminalSessions sessions, AgentConnectionService connections, AgentServerInstance instance, IHubContext<AgentHub> hub)
{
    public async Task<Guid> StartAsync(ClaimsPrincipal user, int deviceId, AgentTerminalShell shell, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceTerminal)).Succeeded
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException();
        if (!System.Enum.IsDefined(shell))
            throw new InvalidOperationException("Choose a supported terminal shell.");

        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) throw new InvalidOperationException("The agent is offline.");
        if (connection.ServerInstanceId != instance.Id)
            throw new InvalidOperationException("Open the terminal on the server instance to which the agent is connected.");

        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct)
            ?? throw new InvalidOperationException("The device is unavailable.");
        var isMac = device.AgentPlatform == AgentPlatform.MacOS;
        if (isMac ? shell != AgentTerminalShell.Zsh : shell is not (AgentTerminalShell.PowerShell or AgentTerminalShell.CommandPrompt))
            throw new InvalidOperationException("The selected shell is not supported on this device.");
        var session = sessions.Create(deviceId, userId, user.Identity?.Name ?? userId.ToString(), device.Name);
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = userId,
                UserName = session.UserName,
                AuditType = AuditType.Update,
                ObjectType = "RemoteTerminal",
                ObjectId = deviceId,
                ObjectName = device.Name,
                ObjectJson = JsonSerializer.Serialize(new { session.Id, Shell = shell.ToString(), Identity = isMac ? "root" : "SYSTEM", Status = "Requested" })
            });
            await db.SaveChangesAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentTerminalReply>(AgentHubProtocol.Terminal,
                new AgentTerminalRequest(session.Id, device.Guid, DateTimeOffset.UtcNow.AddSeconds(30), shell), timeout.Token);
            if (reply is null || reply.SessionId != session.Id || !reply.Accepted)
                throw new InvalidOperationException(reply?.Detail ?? "The agent could not start the terminal. Check its installed version.");
            return session.Id;
        }
        catch
        {
            session.Close();
            throw;
        }
    }

    public async Task StopAsync(ClaimsPrincipal user, Guid id)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceTerminal)).Succeeded
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException();
        var session = sessions.Find(id);
        if (session?.UserId == userId) session.Close();
    }
}
