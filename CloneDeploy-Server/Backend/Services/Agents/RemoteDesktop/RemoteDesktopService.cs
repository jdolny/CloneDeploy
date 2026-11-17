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

/// <summary>Authorizes desktop access and asks the enrolled agent to join a server-owned session.</summary>
public sealed class RemoteDesktopService(ICloneDeployDbFactory factory, IAuthorizationService authorization,
    RemoteDesktopSessions sessions, AgentConnectionService connections, AgentServerInstance instance, IHubContext<AgentHub> hub)
{
    public async Task<Guid> StartAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct)
        => await StartSessionAsync(user, deviceId, null, ct);

    public async Task<AgentDesktopSession[]> ListSessionsAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceRemoteView)).Succeeded) throw new UnauthorizedAccessException();
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null || connection.ServerInstanceId != instance.Id) throw new InvalidOperationException("The agent is not connected to this server.");
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct)
            ?? throw new InvalidOperationException("The device is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentDesktopSession[]>(AgentHubProtocol.DesktopSessions,
            new AgentDesktopSessionsRequest(device.Guid, DateTimeOffset.UtcNow.AddSeconds(30)), timeout.Token);
    }

    public async Task<Guid> StartSessionAsync(ClaimsPrincipal user, int deviceId, int? windowsSessionId, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceRemoteView)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw new UnauthorizedAccessException();
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) throw new InvalidOperationException("The agent is offline.");
        if (connection.ServerInstanceId != instance.Id) throw new InvalidOperationException("Open remote viewing on the server instance to which the agent is connected.");
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct)
            ?? throw new InvalidOperationException("The device is unavailable.");
        var session = sessions.Create(deviceId, userId, user.Identity?.Name ?? userId.ToString(), device.Name);
        session.CanControl = (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceRemoteControl)).Succeeded;
        try
        {
            var available = await ListSessionsAsync(user, deviceId, ct);
            var selected = available.FirstOrDefault(x => windowsSessionId is { } id ? x.Id == id : x.Connection == "Console");
            if (selected is null || !selected.Available) throw new InvalidOperationException("The selected desktop session is unavailable.");
            if (selected.ConsentProtocol != 1) throw new InvalidOperationException("Update the agent before using remote access rules.");
            var effective = await RemoteAccessRules.ResolveAsync(db, deviceId, ct);
            windowsSessionId = selected.Id;
            db.AuditLogs.Add(new AuditLog
            {
                AppUserId = userId, UserName = session.UserName, AuditType = AuditType.Update, ObjectType = "RemoteDesktop",
                ObjectId = deviceId, ObjectName = device.Name, LoggedAtUtc = DateTime.UtcNow,
                ObjectJson = JsonSerializer.Serialize(new { session.Id, deviceId, windowsSessionId, selected.UserName, effective.Mode, effective.Rule, effective.RuleId, Status = "Requested" })
            });
            await db.SaveChangesAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));
            var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentRemoteDesktopReply>(AgentHubProtocol.RemoteDesktop,
                new AgentRemoteDesktopRequest(session.Id, device.Guid, DateTimeOffset.UtcNow.AddSeconds(65))
                { WindowsSessionId = windowsSessionId, ConsentProtocol = 1, AccessMode = effective.Mode, Requester = session.UserName }, timeout.Token);
            db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = session.UserName, AuditType = AuditType.Update, ObjectType = "RemoteDesktop",
                ObjectId = deviceId, ObjectName = device.Name, LoggedAtUtc = DateTime.UtcNow,
                ObjectJson = JsonSerializer.Serialize(new { session.Id, windowsSessionId, Status = reply?.Accepted == true && reply.SessionId == session.Id ? "AccessGranted" : "AccessDenied", reply?.Detail, effective.Mode, effective.Rule }) });
            await db.SaveChangesAsync(ct);
            if (reply is null || reply.SessionId != session.Id || !reply.Accepted)
                throw new InvalidOperationException(reply is { Detail.Length: > 0 and < 1024 } ? reply.Detail : "The agent could not start remote viewing.");
            return session.Id;
        }
        catch
        {
            session.Close();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await hub.Clients.Client(connection.ConnectionId).SendAsync(AgentHubProtocol.RemoteDesktopStop, session.Id, cancel.Token); } catch { }
            throw;
        }
    }
    public async Task StopAsync(ClaimsPrincipal user, Guid id)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceRemoteView)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw new UnauthorizedAccessException();
        var session = sessions.Find(id);
        if (session?.UserId == userId) session.Close();
    }
}
