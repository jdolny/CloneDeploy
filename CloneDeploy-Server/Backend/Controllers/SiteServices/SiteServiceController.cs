using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy.SiteServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.SiteServices;

[ApiController, AllowAnonymous, Route("api/site-services"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SiteServiceController(SiteServiceEnrollmentService enrollment) : ControllerBase
{
    private const int EnrollmentRequestBytes = 24576;
    private const int StatusReportRequestBytes = 4096;
    private const int StorageLeaseRequestBytes = 12288;
    private const int EmptyRequestBytes = 0;

    [HttpPost("enroll"), RequestSizeLimit(EnrollmentRequestBytes), EnableRateLimiting("SiteEnrollment")]
    public async Task<IActionResult> Enroll(SiteEnrollmentRequest request, CancellationToken ct) => await Execute(async () =>
        Ok(await enrollment.EnrollAsync(request, ct)));

    [HttpPost("status"), RequestSizeLimit(StatusReportRequestBytes)]
    public async Task<IActionResult> Status(SiteStatusRequest request, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        return certificate is null ? Unauthorized() : Ok(await enrollment.StatusAsync(certificate, request, ct));
    });

    [HttpGet("boot/manifest")]
    public async Task<IActionResult> BootManifest([FromServices] SiteBootFiles bootFiles, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        await enrollment.AuthorizeBootAsync(certificate, ct);
        return Ok(await bootFiles.ManifestAsync(ct));
    });

    [HttpGet("boot/file")]
    public async Task<IActionResult> BootFile([FromQuery] string path, [FromServices] SiteBootFiles bootFiles, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        await enrollment.AuthorizeBootAsync(certificate, ct);
        try { return File(bootFiles.Open(path), "application/octet-stream"); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    });

    [HttpGet("storage/manifest")]
    public async Task<IActionResult> StorageManifest([FromServices] SiteStorageService storage, [FromServices] ReplicationSettingsStore settings, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        var site = await enrollment.AuthorizeCapabilityAsync(certificate, false, ct);
        var limits = await settings.ReadAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok((await storage.ManifestAsync(site, ct, Request.Query["smb"] == "1", Request.Query["linuxMount"] == "1")) with { SharedFileConcurrency = limits.Global.SharedFiles });
    });

    [HttpGet("storage/file")]
    public async Task<IActionResult> StorageFile([FromQuery] int provider, [FromQuery] string key, [FromServices] SiteStorageService storage,
        [FromServices] ReplicationTransfers transfers, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        try
        {
            var site = await enrollment.AuthorizeCapabilityAsync(certificate, false, ct);
            var input = await storage.OpenAsync(site, provider, key, ct);
            var handedOff = false;
            try
            {
                var slot = await transfers.TryEnterAsync(site, key.StartsWith("image-"), ct);
                if (slot is null)
                {
                    Response.Headers.RetryAfter = ControllerRetryLimits.MinDelaySeconds.ToString();
                    return StatusCode(StatusCodes.Status429TooManyRequests);
                }
                var result = File(transfers.Wrap(input, site, slot), "application/octet-stream");
                handedOff = true;
                return result;
            }
            finally { if (!handedOff) await input.DisposeAsync(); }
        }
        catch (IOException) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
    });

    [HttpPost("storage/report"), RequestSizeLimit(StatusReportRequestBytes)]
    public async Task<IActionResult> StorageReport(SiteStorageReport report, [FromServices] SiteStorageService storage, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        await storage.ReportAsync(await enrollment.AuthorizeCapabilityAsync(certificate, false, ct), report, ct);
        return NoContent();
    });

    [HttpPost("storage/redeem"), RequestSizeLimit(StorageLeaseRequestBytes)]
    public async Task<IActionResult> RedeemDownload(SiteDownloadRedemption request, [FromServices] SiteDownloadTickets tickets, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        return Ok(await tickets.RedeemAsync(await enrollment.AuthorizeCapabilityAsync(certificate, false, ct), request, ct));
    });

    [HttpPost("storage/abandon"), RequestSizeLimit(StorageLeaseRequestBytes)]
    public async Task<IActionResult> AbandonDownload(SiteDownloadRedemption request, [FromServices] SiteDownloadTickets tickets, CancellationToken ct) => await Execute(async () =>
    {
        await tickets.AbandonAsync(request.Ticket, HttpContext.Connection.RemoteIpAddress ?? throw new UnauthorizedAccessException(), ct);
        return NoContent();
    });

    [HttpPost("storage/reads/{id:guid}"), RequestSizeLimit(EmptyRequestBytes)]
    public async Task<IActionResult> RenewRead(Guid id, [FromServices] SiteStorageService storage, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        await storage.UpdateReadAsync(await enrollment.AuthorizeCapabilityAsync(certificate, false, ct), id, false, ct);
        return NoContent();
    });

    [HttpDelete("storage/reads/{id:guid}")]
    public async Task<IActionResult> ReleaseRead(Guid id, [FromServices] SiteStorageService storage, CancellationToken ct) => await Execute(async () =>
    {
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct);
        if (certificate is null) return Unauthorized();
        await storage.UpdateReadAsync(await enrollment.AuthorizeCapabilityAsync(certificate, false, ct), id, true, ct);
        return NoContent();
    });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> operation)
    {
        if (!Request.IsHttps) return BadRequest("HTTPS is required.");
        try { return await operation(); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or FormatException or ArgumentException)
        { return BadRequest("Invalid enrollment or service request."); }
    }
}
