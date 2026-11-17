using System.Security.Cryptography;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Route("api/imaging/secure/handshake"), AllowAnonymous, EnableRateLimiting("ImagingLogin")]
public sealed class ImagerSessionController(ImagerSessions sessions) : ControllerBase
{
    [HttpPost, RequestSizeLimit(2048)]
    public IActionResult Create(ImagerSessionProtocol.Hello hello)
    {
        if (!Request.IsHttps) return BadRequest();
        try { return Ok(sessions.Create(hello)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return BadRequest(); }
        catch (InvalidOperationException) { return StatusCode(429); }
    }
}
