using System.Net.Http.Headers;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

// Every operation validates the protected deployment token and its live device-specific lease.
[ApiController, Route("api/imaging/deployment"), AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("ImagingDeploymentClaim")]
public sealed class ImagingDeploymentExecutionController(ImagingDeploymentClaims claims, StorageContentStorage storage) : ControllerBase
{
    private const int OrganizationalUnitRequestBytes = 8192;
    private const int SysprepCredentialsRequestBytes = 65536;
    private const int DeploymentProgressRequestBytes = 1024;
    private const int DeploymentFinishRequestBytes = 1048576;
    private const int MaxDownloadRangeBytes = 16 * 1024 * 1024;

    [HttpPost("ou"), RequestSizeLimit(OrganizationalUnitRequestBytes)]
    public Task<IActionResult> Ou(CloneDeploy_Agent.Contracts.ImagingOuRequest request, CancellationToken ct) =>
        Execute(async () => Ok(await claims.ResolveOuAsync(Bearer(), request, ct)));

    [HttpPost("sysprep-credentials"), RequestSizeLimit(SysprepCredentialsRequestBytes)]
    public Task<IActionResult> SysprepCredentials([FromBody] string ticket, [FromServices] ImagePrepCredentialService credentials,
        [FromServices] ISecretProtector secrets, CancellationToken ct) =>
        Execute(async () => Ok(await claims.ResolveSysprepCredentialsAsync(Bearer(), ticket, credentials, secrets, ct)));
    [HttpGet("execution")]
    public Task<IActionResult> Execution(CancellationToken ct) => Execute(async () =>
    {
        var image = await claims.ExecutionAsync(Bearer(), storage, ct);
        if (image.ConfigureDomainJoinOu && Request.Headers["X-Imager-OU"] != "1")
            throw new InvalidOperationException("This deployment requires OU resolution. Rebuild WinPE with the current Imager.");
        return Ok(image);
    });
    [HttpGet("smb")]
    public Task<IActionResult> Smb([FromServices] ISecretProtector secrets, CancellationToken ct) => Execute(async () => Ok(await claims.ExecutionSmbAsync(Bearer(), secrets, ct)));
    [HttpGet("image")]
    public Task<IActionResult> Image(CancellationToken ct) => Execute(async () => File(await claims.OpenExecutionImageAsync(Bearer(), storage, ct), "application/octet-stream"));

    [HttpGet("drivers/{archiveId:guid}")]
    public Task<IActionResult> Driver(Guid archiveId, [FromServices] DriverPreparationStorage drivers, CancellationToken ct) =>
        Execute(async () => File(await claims.OpenDriverAsync(Bearer(), archiveId, drivers, storage, ct), "application/zip"));
    [HttpGet("ffu"), DisableRateLimiting]
    public Task<IActionResult> Ffu(CancellationToken ct) => Execute(async () =>
    {
        var source = await claims.OpenExecutionFfuAsync(Bearer(), storage, ct);
        var handedOff = false;
        try
        {
            if (Request.Headers.IfRange.Count != 0 || !RangeHeaderValue.TryParse(Request.Headers.Range.ToString(), out var range) ||
                range.Unit != "bytes" || range.Ranges.Count != 1 || range.Ranges.Single() is not { From: { } from, To: { } to } ||
                from < 0 || to < from || to - from >= MaxDownloadRangeBytes) return BadRequest();
            if (from >= source.Image.SizeBytes || to >= source.Image.SizeBytes)
            { Response.Headers.ContentRange = $"bytes */{source.Image.SizeBytes}"; return StatusCode(StatusCodes.Status416RangeNotSatisfiable); }
            var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + source.Image.Sha256.ToLowerInvariant() + '"');
            if (Request.Headers.IfMatch.ToString() != etag.ToString()) return StatusCode(StatusCodes.Status412PreconditionFailed);
            var result = new FileStreamResult(source.Stream, "application/octet-stream") { EnableRangeProcessing = true, EntityTag = etag };
            handedOff = true;
            return result;
        }
        finally { if (!handedOff) await source.Stream.DisposeAsync(); }
    });
    [HttpPost("progress"), RequestSizeLimit(DeploymentProgressRequestBytes)]
    public Task<IActionResult> Progress(ImagingProgressDto progress, CancellationToken ct) => Execute(async () => { await claims.ReportExecutionProgressAsync(Bearer(), progress, ct); return NoContent(); });
    [HttpPost("renew")]
    public Task<IActionResult> Renew(CancellationToken ct) => Execute(async () => Ok(await claims.RenewExecutionAsync(Bearer(), ct)));
    [HttpPost("start")]
    public Task<IActionResult> Start(CancellationToken ct) => Execute(async () => { await claims.SetExecutionStateAsync(Bearer(), "Started", ct); return NoContent(); });
    [HttpPost("multicast/start")]
    public Task<IActionResult> StartMulticast(CancellationToken ct) => Execute(async () => { await claims.StartMulticastAsync(Bearer(), ct); return NoContent(); });
    [HttpPost("queue")]
    public Task<IActionResult> Queue(CancellationToken ct) => Execute(async () => Ok(await claims.EnterQueueAsync(Bearer(), ct)));
    [HttpPost("finish"), RequestSizeLimit(DeploymentFinishRequestBytes)]
    public Task<IActionResult> Finish(DeploymentFinishDto request, CancellationToken ct) => Execute(async () =>
    { await claims.SetExecutionStateAsync(Bearer(), request.Success ? "Completed" : "Failed", ct, request.Diagnostics, request.AgentInstalled); return NoContent(); });
    private string? Bearer() => AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var value) &&
        value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? value.Parameter : null;
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> operation)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Deployment requires HTTPS.");
        try { return await operation(); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (InvalidOperationException ex) { return Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message); }
        catch (IOException) { return Problem(statusCode: StatusCodes.Status409Conflict, detail: "The assigned image is unavailable or invalid. Check its storage provider."); }
    }
}
