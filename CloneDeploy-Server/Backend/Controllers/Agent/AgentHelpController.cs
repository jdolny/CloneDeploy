using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Route("api/agent/v1/help")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentHelpController(HelpDeskService help) : ControllerBase
{

    [HttpPost, RequestSizeLimit(HelpAttachmentLimits.PayloadBytes)]
    public async Task<ActionResult<HelpResponse>> RequestHelp([FromBody] HelpAgentRequest input, CancellationToken ct)
    {
        if (!Request.IsHttps || input.Request is null ||
            !int.TryParse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim), out var deviceId)) return BadRequest();
        try { return await help.AgentAsync(deviceId, input.Sid, input.Request, ct); }
        catch (UnauthorizedAccessException) { return StatusCode(StatusCodes.Status403Forbidden, new HelpResponse("This help request is not available to the signed-in user.")); }
        catch (InvalidOperationException ex) { return BadRequest(new HelpResponse(ex.Message)); }
    }
}

