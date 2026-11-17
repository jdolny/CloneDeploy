using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.RemoteAccess;

/// <summary>Authenticates each side of a desktop stream before attaching it to its single-use session slot.</summary>
[ApiController]
public sealed class RemoteDesktopController(RemoteDesktopSessions sessions) : ControllerBase
{
    // WebSockets use GET on HTTP/1.1 and extended CONNECT on HTTP/2.
    [Route("api/ui/remote-desktop/{id:guid}/viewer"), Authorize(Policy = Constants.Permissions.DeviceRemoteView)]
    public Task Viewer(Guid id) => ConnectAsync(id, false);

    [Route("api/agent/v1/remote/{id:guid}"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public Task Agent(Guid id) => ConnectAsync(id, true);

    private async Task ConnectAsync(Guid id, bool agent)
    {
        // Preserve failed handshake status codes instead of rendering an HTML error page.
        var statusPages = HttpContext.Features.Get<IStatusCodePagesFeature>();
        if (statusPages is not null) statusPages.Enabled = false;
        if (!Request.IsHttps || !HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        // Browser cookies alone must not authorize cross-origin WebSocket handshakes.
        if (!agent && (!Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority), $"{Request.Scheme}://{Request.Host}", StringComparison.OrdinalIgnoreCase)))
        { Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        var session = sessions.Find(id);
        var claim = agent ? AgentCertificateAuthentication.DeviceIdClaim : ClaimTypes.NameIdentifier;
        if (session is null || !int.TryParse(User.FindFirstValue(claim), out var owner) ||
            owner != (agent ? session.DeviceId : session.UserId)) { Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!agent)
        {
            var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            session.CanControl = session.CanControl &&
                (await authorization.AuthorizeAsync(User, Constants.Permissions.DeviceRemoteControl)).Succeeded;
        }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        if (!session.Attach(socket, agent)) { socket.Abort(); return; }
        using var aborted = HttpContext.RequestAborted.Register(session.Close);
        await session.Completed.Task;
    }
}
