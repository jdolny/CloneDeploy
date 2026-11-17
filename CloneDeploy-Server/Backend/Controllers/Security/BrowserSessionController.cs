using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Security;

[ApiController]
public sealed class BrowserSessionController : ControllerBase
{
    [HttpGet(BrowserSession.Path), AllowAnonymous]
    public async Task<IActionResult> Get()
    {
        Response.Headers.CacheControl = "no-store";
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var userId = result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return result.Succeeded && !string.IsNullOrEmpty(userId)
            ? Ok(new { userId }) : Unauthorized();
    }
}
