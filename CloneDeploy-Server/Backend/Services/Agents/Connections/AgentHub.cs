using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Keeps a live connection between device agents and the server.
/// </summary>
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
public sealed class AgentHub(AgentConnectionService connections, AgentActivityService activity, HelpPresenceService help, ILogger<AgentHub> logger, CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory factory) : Hub
{
    private int DeviceId => int.Parse(Context.User!.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
    private Guid CertificateId => Guid.Parse(Context.User!.FindFirstValue(AgentCertificateAuthentication.CertificateIdClaim)!);

    public override async Task OnConnectedAsync()
    {
        await connections.ConnectAsync(DeviceId, CertificateId, Context.ConnectionId, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task Heartbeat()
    {
        if (!await connections.HeartbeatAsync(DeviceId, CertificateId, Context.ConnectionId, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("This agent connection is no longer authorized or has been replaced.");
        }
    }

    public async Task<AgentFeatures> HelpPresence(string[] sids)
    {
        await Heartbeat();
        await using var db = await factory.CreateContextAsync();
        var features = await AgentFeatureSettings.FeaturesAsync(db, Context.ConnectionAborted);
        if (!features.HelpDeskEnabled) { help.Remove(Context.ConnectionId); return features; }
        // This optional subscription cannot abort or change the device connection.
        try { await help.RegisterAsync(DeviceId, Context.ConnectionId, sids, Context.ConnectionAborted); }
        catch (Exception ex) { logger.LogDebug(ex, "Help presence registration failed."); }
        return features;
    }

    public async Task PortalHelpPresence(AgentPortalSessionRequest[] sessions)
    {
        await Heartbeat();
        if (sessions is null || sessions.Length > 128) return;
        await using var db = await factory.CreateContextAsync();
        if (!(await AgentFeatureSettings.FeaturesAsync(db, Context.ConnectionAborted)).HelpDeskEnabled)
        { help.Remove(Context.ConnectionId); return; }
        var device = Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var validator = Context.GetHttpContext()!.RequestServices.GetRequiredService<AgentPortalSessions>();
        var sids = new List<string>();
        foreach (var input in sessions)
        {
            if (input is null) continue;
            var session = await validator.ValidateAsync(device, input, factory, Context.ConnectionAborted);
            if (session is null) continue;
            var sid = await db.DirectoryObjects.Where(x => x.DirectorySourceId == session.User.DirectorySourceId &&
                x.ObjectGuid == session.User.ObjectGuid).Select(x => x.ObjectSid).SingleOrDefaultAsync(Context.ConnectionAborted);
            if (!string.IsNullOrWhiteSpace(sid)) sids.Add(sid);
        }
        await help.RegisterAsync(DeviceId, Context.ConnectionId, sids.ToArray(), Context.ConnectionAborted);
    }

    public async Task ReportActivity(AgentActivity report)
    {
        // Revalidate certificate and socket ownership just as for an existing heartbeat.
        if (!await connections.HeartbeatAsync(DeviceId, CertificateId, Context.ConnectionId, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("This agent connection is no longer authorized or has been replaced.");
        }
        if (!await activity.ReportAsync(DeviceId, CertificateId, Context.ConnectionId, report, Context.ConnectionAborted))
            throw new HubException("Invalid activity report.");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        help.Remove(Context.ConnectionId);
        await connections.DisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
