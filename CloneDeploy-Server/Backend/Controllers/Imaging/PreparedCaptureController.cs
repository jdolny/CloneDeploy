using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

// A random, short-lived capability authorizes only this previously created upload.
// It cannot sign in, list images, create another upload, or deploy an image.
[ApiController, Route("api/imaging/prepared-capture/uploads/{id:guid}"), AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PreparedCaptureController(PreparedCaptureService captures, ImagingUploadService uploads,
    ILogger<PreparedCaptureController> logger) : ControllerBase
{
    private const int CaptureProgressRequestBytes = 1024;
    private const int CaptureCompleteRequestBytes = 4096;

    [HttpGet]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Execute(id, row => Task.FromResult<IActionResult>(Ok(captures.Destination(row))), ct);

    [HttpPost("progress"), RequestSizeLimit(CaptureProgressRequestBytes)]
    public Task<IActionResult> Progress(Guid id, ImagingProgressDto request, CancellationToken ct) => Execute(id, async row =>
    { await uploads.ProgressAsync(id, row.UserId, request, ct); return NoContent(); }, ct);

    [HttpPut("content"), RequestSizeLimit(ImagingUploadService.ChunkBytes)]
    public Task<IActionResult> Content(Guid id, [FromQuery] long offset, CancellationToken ct) => Execute(id, async row =>
    { await uploads.AppendAsync(id, row.UserId, offset, Request.Body, ct); return NoContent(); }, ct);

    [HttpPost("complete"), RequestSizeLimit(CaptureCompleteRequestBytes)]
    public Task<IActionResult> Complete(Guid id, CompleteImageUploadDto request, CancellationToken ct) => Execute(id,
        async row => Ok(await uploads.CompleteAsync(id, row.UserId, request, ct)), ct);

    [HttpPost("ffu"), RequestSizeLimit(ImagingUploadService.ChunkBytes)]
    public Task<IActionResult> Ffu(Guid id, [FromQuery] long sequence, [FromQuery] string operation, [FromQuery] long offset, CancellationToken ct) => Execute(id,
        async row => Ok(await uploads.MutateFfuAsync(id, row.UserId, sequence, operation, offset, Request.Body, ct)), ct);

    [HttpPost("logs"), RequestSizeLimit(1048576)]
    public async Task<IActionResult> CaptureLog(Guid id, CaptureLogDto request, CancellationToken ct)
    {
        if (!Request.IsHttps) return Unauthorized();
        var row = await captures.AuthorizeCompletedLogAsync(id, Request.Headers["X-CloneDeploy-Capture"].ToString(), ct);
        if (row is null) return Unauthorized();
        try { await uploads.SaveCompletedLogAsync(id, row.UserId, request, ct); return NoContent(); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("ffu")]
    public Task<IActionResult> ReadFfu(Guid id, [FromQuery] long offset, [FromQuery] int count, CancellationToken ct) => Execute(id,
        async row => File(await uploads.ReadFfuUploadAsync(id, row.UserId, offset, count, ct), "application/octet-stream"), ct);

    [HttpDelete]
    public Task<IActionResult> Abort(Guid id, CancellationToken ct) => Execute(id, async row =>
    { await uploads.AbortAsync(id, row.UserId, ct, "Failed"); return NoContent(); }, ct);

    private async Task<IActionResult> Execute(Guid id, Func<ImageUpload, Task<IActionResult>> action, CancellationToken ct)
    {
        if (!Request.IsHttps) return Unauthorized();
        var row = await captures.AuthorizeAsync(id, Request.Headers["X-CloneDeploy-Capture"].ToString(), ct);
        if (row is null) return Unauthorized();
        try { return await action(row); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogError(ex, "Prepared capture storage failed"); return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Image storage is unavailable." }); }
    }
}
