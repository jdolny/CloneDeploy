using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Route("api/imaging"), Authorize(Policy = ImagingAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ImagingUploadsController(ImagingUploadService uploads, ImagingDeviceService devices, ILogger<ImagingUploadsController> logger) : ControllerBase
{
    private const int CaptureCommandRequestBytes = 4096;
    private const int DeviceRequestBytes = 32768;
    private const int DeviceLogsRequestBytes = 1048576;
    private const int CreateUploadRequestBytes = 8192;
    private const int UploadProgressRequestBytes = 1024;

    [HttpGet("capture-images")]
    public Task<IActionResult> Images(CancellationToken ct) => ExecuteWithPermission(Constants.Permissions.ImageView, async () => Ok(await uploads.ImagesAsync(ct)), ct);

    [HttpPost("capture-images"), RequestSizeLimit(CaptureCommandRequestBytes)]
    public Task<IActionResult> CreateImage(CreateCaptureImageDto request, CancellationToken ct) =>
        ExecuteWithPermission(Constants.Permissions.ImageCreate, async () => Ok(await uploads.CreateImageAsync(request, UserId, ct)), ct);

    [HttpPost("device"), RequestSizeLimit(DeviceRequestBytes)]
    public Task<IActionResult> Device(ImagingDeviceRequestDto request, CancellationToken ct) =>
        ExecuteWithPermission(Constants.Permissions.ImagingAccess, async () => Ok(new { device = await devices.ResolveAsync(request, UserId, ct) }), ct);

    [HttpPost("device/logs"), RequestSizeLimit(DeviceLogsRequestBytes)]
    public Task<IActionResult> FailureLog(ImagingFailureLogDto request, CancellationToken ct) => ExecuteWithPermission(Constants.Permissions.ImagingAccess, async () =>
    {
        await devices.SaveFailureLogAsync(request, UserId, ct);
        return NoContent();
    }, ct);

    public sealed record RenameDeviceRequest(string DeviceLinkToken, string Name);

    [HttpPost("device/rename"), RequestSizeLimit(CaptureCommandRequestBytes)]
    public Task<IActionResult> RenameDevice(RenameDeviceRequest request, [FromServices] DeviceRenameService rename, CancellationToken ct) => ExecuteWithPermission(Constants.Permissions.DeviceEdit, async () =>
    {
        var deviceId = devices.ReadLink(request.DeviceLinkToken, UserId);
        var result = await rename.RenameAsync(User, deviceId, request.Name, false, ct: ct);
        return result.Success ? Ok(result) : Conflict(new { message = result.Detail });
    }, ct);

    [HttpPost("images/{imageId:int}/uploads"), RequestSizeLimit(CreateUploadRequestBytes)]
    public Task<IActionResult> Begin(int imageId, BeginImageUploadDto request, CancellationToken ct) =>
        Execute(async () => Ok(await uploads.BeginAsync(imageId, UserId, devices.ReadLink(request.DeviceLinkToken, UserId), ct, request.Format)), ct);

    [HttpPost("uploads/{id:guid}/progress"), RequestSizeLimit(UploadProgressRequestBytes)]
    public Task<IActionResult> Progress(Guid id, ImagingProgressDto progress, CancellationToken ct) => Execute(async () =>
    {
        await uploads.ProgressAsync(id, UserId, progress, ct);
        return NoContent();
    }, ct);

    [HttpPost("uploads/{id:guid}/renew"), RequestSizeLimit(CaptureCommandRequestBytes)]
    public Task<IActionResult> Renew(Guid id, [FromServices] ImagingTokens tokens, CancellationToken ct) => Execute(async () =>
    {
        if (User.FindFirstValue("imaging_kind") != "user") return Forbid(ImagingAuthentication.SchemeName);
        // Completion can race this heartbeat. Do not issue a token for inactive or
        // unowned uploads; let the normal progress request determine task state.
        if (!await uploads.CanRenewCaptureAsync(id, UserId, ct)) return NoContent();
        var bearer = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(Request.Headers.Authorization.ToString());
        return Ok(tokens.RenewCapture(bearer.Parameter!));
    }, ct);

    [HttpPut("uploads/{id:guid}/content"), RequestSizeLimit(ImagingUploadService.ChunkBytes)]
    public Task<IActionResult> Content(Guid id, [FromQuery] long offset, CancellationToken ct) => Execute(async () =>
    {
        await uploads.AppendAsync(id, UserId, offset, Request.Body, ct);
        return NoContent();
    }, ct);

    [HttpPost("uploads/{id:guid}/complete"), RequestSizeLimit(CaptureCommandRequestBytes)]
    public Task<IActionResult> Complete(Guid id, CompleteImageUploadDto request, CancellationToken ct) =>
        Execute(async () => Ok(await uploads.CompleteAsync(id, UserId, request, ct)), ct);

    [HttpPost("uploads/{id:guid}/logs"), RequestSizeLimit(DeviceLogsRequestBytes)]
    public Task<IActionResult> CaptureLog(Guid id, CaptureLogDto request, CancellationToken ct) => Execute(async () =>
    {
        await uploads.SaveCompletedLogAsync(id, UserId, request, ct);
        return NoContent();
    }, ct);

    [HttpPost("uploads/{id:guid}/ffu"), RequestSizeLimit(ImagingUploadService.ChunkBytes)]
    public Task<IActionResult> FfuWrite(Guid id, [FromQuery] long sequence, [FromQuery] string operation, [FromQuery] long offset, CancellationToken ct) =>
        Execute(async () => !Request.IsHttps ? BadRequest() : Ok(await uploads.MutateFfuAsync(id, UserId, sequence, operation, offset, Request.Body, ct)), ct);

    [HttpGet("uploads/{id:guid}/ffu")]
    public Task<IActionResult> FfuRead(Guid id, [FromQuery] long offset, [FromQuery] int count, CancellationToken ct) =>
        Execute(async () => !Request.IsHttps ? BadRequest() : File(await uploads.ReadFfuUploadAsync(id, UserId, offset, count, ct), "application/octet-stream"), ct);

    [HttpDelete("uploads/{id:guid}")]
    public Task<IActionResult> Abort(Guid id, [FromQuery] string? reason, CancellationToken ct) => Execute(async () =>
    {
        await uploads.AbortAsync(id, UserId, ct, reason == "Failed" ? "Failed" : "Cancelled");
        return NoContent();
    }, ct);

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Task<IActionResult> Execute(Func<Task<IActionResult>> action, CancellationToken ct) =>
        ExecuteWithPermission(Constants.Permissions.ImageCapture, action, ct);

    private async Task<IActionResult> ExecuteWithPermission(string permission, Func<Task<IActionResult>> action, CancellationToken ct)
    {
        if (!await uploads.AllowedAsync(User, permission, ct)) return StatusCode(StatusCodes.Status403Forbidden, new { error = "imaging_forbidden", message = "The requested imaging permission is required." });
        try { return await action(); }
        catch (KeyNotFoundException) { return NotFound(new { error = "upload_not_found" }); }
        catch (InvalidDataException ex) { return BadRequest(new { error = "invalid_upload", message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = "upload_conflict", message = ex.Message }); }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "imaging_forbidden", message = "The requested imaging permission is required." });
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Image upload storage operation failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "upload_storage_unavailable", message = "Image storage is unavailable. Check the server log and storage provider." });
        }
    }
}
