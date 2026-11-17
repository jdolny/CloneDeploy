using System.Net.Http.Headers;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

// Deployment credentials deliberately do not authenticate to ImagingBearer or browser policies.
[ApiController, Route("api/imaging/deployment"), AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("ImagingDeploymentClaim")]
public sealed class ImagingDeploymentClaimController(ImagingDeploymentClaims claims) : ControllerBase
{
    private const int DeploymentClaimRequestBytes = 16384;
    private const int MaxAuthorizationHeaderLength = 8192;

    [HttpPost("claim"), RequestSizeLimit(DeploymentClaimRequestBytes)]
    public async Task<IActionResult> Claim(ImagingDeploymentClaimDto request, CancellationToken ct)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Deployment claims require HTTPS.");
        try
        {
            var result = await claims.ClaimAsync(request, ct);
            return result is null ? NoContent() : Ok(result);
        }
        catch (ArgumentException ex) { return Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message); }
    }
    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken ct)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Deployment sessions require HTTPS.");
        var result = await claims.ReadAsync(Bearer(), ct);
        return result is null ? Unauthorized() : Ok(result);
    }
    [HttpPost("release")]
    public async Task<IActionResult> Release(CancellationToken ct)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Deployment sessions require HTTPS.");
        return await claims.ReleaseAsync(Bearer(), ct) ? NoContent() : Unauthorized();
    }
    private string? Bearer()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.Length <= MaxAuthorizationHeaderLength && AuthenticationHeaderValue.TryParse(header, out var value) &&
            string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ? value.Parameter : null;
    }
}
