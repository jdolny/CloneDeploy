using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController]
[Route("api/agent/v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentController(AgentEnrollmentService enrollment, AgentCheckinService checkins) : ControllerBase
{
    private const int InventoryRequestBytes = 10 * 1024 * 1024;
    private const int EnrollmentRequestBytes = 24_576;
    private const int CheckInRequestBytes = 4 * 1024 * 1024;
    private const int Sha256HexLength = 64;

    [HttpGet("features"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> Features([FromServices] CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        await using var db = await factory.CreateContextAsync();
        return Ok(await AgentFeatureSettings.FeaturesAsync(db, ct));
    }

    [HttpGet("branding"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> Branding([FromServices] CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory factory, CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        await using var db = await factory.CreateContextAsync();
        return Ok(await BrandingSettings.ReadAsync(db, ct));
    }

    [HttpGet("mac-updates/{hash}"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> MacUpdate(string hash, [FromServices] MacAgentReleaseService releases, CancellationToken ct)
    {
        if (!Request.IsHttps || hash.Length != Sha256HexLength || !hash.All(Uri.IsHexDigit)) return BadRequest();
        var release = await releases.CurrentAsync(ct);
        if (release is null || !string.Equals(release.Sha256, hash, StringComparison.OrdinalIgnoreCase)) return NotFound();
        var file = await releases.OpenMatchingAsync(hash, ct);
        return file is null ? NotFound() : File(file, "application/octet-stream", enableRangeProcessing: true);
    }

    [HttpGet("updates/{hash}"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    public async Task<IActionResult> Update(string hash, [FromServices] AgentReleaseService releases, CancellationToken ct, [FromQuery] string architecture = "x64")
    {
        if (!Request.IsHttps) return BadRequest();
        if (architecture is not ("x64" or "arm64")) return BadRequest();
        var file = await releases.OpenAsync(hash, ct, architecture);
        return file is null ? NotFound() : File(file, "application/x-msi", enableRangeProcessing: true);
    }
    [HttpPost("inventory")]
    [TypeFilter(typeof(AgentInventoryAdmissionFilter))]
    [Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    [RequestSizeLimit(InventoryRequestBytes)]
    public async Task<IActionResult> Inventory(AgentInventory inventory, [FromServices] AgentInventoryService service, CancellationToken ct)
    {
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        try
        {
            return await service.SaveAsync(deviceId, inventory, ct) ? NoContent() : Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Device is unavailable.");
        }
        catch (ArgumentException ex) { return Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Message); }
    }

    [HttpPost("enroll")]
    [AllowAnonymous]
    [EnableRateLimiting("AgentEnrollment")]
    [RequestSizeLimit(EnrollmentRequestBytes)]
    public async Task<ActionResult<AgentEnrollmentResponseDto>> Enroll(AgentEnrollmentRequestDto request, CancellationToken ct)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "HTTPS is required.");
        try { return Ok(await enrollment.EnrollAsync(request, ct, HttpContext.Connection.RemoteIpAddress?.ToString())); }
        catch (AgentEnrollmentPendingException ex)
        {
            Response.Headers.RetryAfter = ex.Pending.RetryAfterSeconds.ToString();
            return Accepted(ex.Pending);
        }
        catch (AgentEnrollmentException ex) { return Problem(statusCode: ex.StatusCode, detail: ex.Message); }
    }

    [HttpPost("check-in")]
    [Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
    [RequestSizeLimit(CheckInRequestBytes)]
    public async Task<ActionResult<AgentCheckinResponseDto>> CheckIn(AgentCheckinRequestDto request, CancellationToken ct,
        [FromServices] AgentReleaseService releases, [FromServices] CloneDeploy_Server.Backend.Data.ICloneDeployDbFactory factory,
        [FromServices] MacAgentReleaseService macReleases)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "HTTPS is required.");
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        var deviceGuid = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await checkins.CheckInAsync(deviceId, deviceGuid, request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (result.Success)
        {
            AgentCertificateRenewal? renewal = null;
            if (request.SupportsCertificateRenewal)
            {
                try { renewal = await enrollment.RenewCertificateAsync(deviceId, Guid.Parse(User.FindFirstValue(AgentCertificateAuthentication.CertificateIdClaim)!), ct, request.CertificateRenewalCsr); }
                catch (AgentEnrollmentException ex) when (ex.StatusCode == 503)
                {
                    // Keep normal device work available while administrators fix CA readiness.
                    HttpContext.RequestServices.GetRequiredService<ILogger<AgentController>>()
                        .LogWarning("Certificate renewal deferred for device {DeviceId}: {Reason}", deviceId, ex.Message);
                }
                catch (AgentEnrollmentException ex) when (ex.StatusCode == 403) { return Forbid(AgentCertificateAuthentication.SchemeName); }
                catch (AgentEnrollmentException ex) when (ex.StatusCode is 400 or 409) { return Problem(statusCode: ex.StatusCode, detail: ex.Message); }
            }
            await using var db = await factory.CreateContextAsync();
            var branding = await BrandingSettings.ReadAsync(db, ct);
            return Ok(result.Data! with { CertificateRenewal = renewal, Update = request.Platform == AgentPlatform.Windows ? await releases.CurrentAsync(ct, request.Architecture) : null, MacUpdate = request.Platform == AgentPlatform.MacOS && request.SupportsMacPackageUpdates ? await macReleases.CurrentAsync(ct) : null, Platform = request.Platform, BrandingVersion = branding.Version, Features = await AgentFeatureSettings.FeaturesAsync(db, ct) });
        }
        var status = result.Error.Code switch
        {
            ServiceErrorCodes.Invalid => StatusCodes.Status400BadRequest,
            ServiceErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            ServiceErrorCodes.Busy => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status500InternalServerError
        };
        if (status == StatusCodes.Status429TooManyRequests) Response.Headers.RetryAfter = Random.Shared.Next(ControllerRetryLimits.MinDelaySeconds, ControllerRetryLimits.MaxDelaySeconds + 1).ToString();
        return Problem(statusCode: status, detail: result.Error.Args.FirstOrDefault() as string ?? "Check-in could not be completed.");
    }
}
