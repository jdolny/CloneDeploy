using CloneDeploy_Server.Backend.Services;
using CloneDeploy.SiteServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.SiteServices;

// These routes authenticate the enrolled TLS certificate in Site(), not a web UI session.
[ApiController, AllowAnonymous, Route("api/site-services/multicast")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MulticastSenderController(SiteServiceEnrollmentService enrollment, MulticastQueue queue) : ControllerBase
{
    private const int ClaimRequestBytes = 1024;
    private const int ReportRequestBytes = 8192;

    private async Task<Guid> Site(CancellationToken ct)
    {
        if (!Request.IsHttps) throw new UnauthorizedAccessException();
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct) ?? throw new UnauthorizedAccessException();
        return await enrollment.AuthorizeCapabilityAsync(certificate, true, ct);
    }
    [HttpPost("claim"), RequestSizeLimit(ClaimRequestBytes)]
    public async Task<IActionResult> Claim(CancellationToken ct)
    {
        try { var job = await queue.ClaimAsync(await Site(ct), ct); return job is null ? NoContent() : Ok(job); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }
    [HttpPost("{id:guid}/{lease:guid}/report"), RequestSizeLimit(ReportRequestBytes)]
    public async Task<IActionResult> Report(Guid id, Guid lease, MulticastProgress progress, CancellationToken ct)
    {
        try { return Ok(await queue.ReportAsync(await Site(ct), id, lease, progress, ct)); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }
}
