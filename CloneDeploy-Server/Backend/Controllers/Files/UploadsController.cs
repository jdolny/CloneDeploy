using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CloneDeploy_Server.Backend.Controllers.Files;

[ApiController]
[Authorize]
[Route("api/ui/uploads")]
[AutoValidateAntiforgeryToken]
[RequestSizeLimit(FileService.ChunkSize + UploadRequestLimits.ChunkOverheadBytes)]
public sealed class UploadsController(FileService files, IAntiforgery antiforgery,
    IStringLocalizer<LocalizationResources> localizer) : Controller
{
    [HttpGet("token")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Token() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost]
    public async Task<IActionResult> Start(UploadRequestDto request, CancellationToken ct)
    {
        var result = await files.StartAsync(User, request, ct);
        return result.Success ? Ok(result.Data) : UploadProblem(result.Error);
    }

    [HttpPut("{id:guid}/chunks")]
    public async Task<IActionResult> Chunk(Guid id, [FromQuery] long offset, CancellationToken ct)
    {
        var result = await files.AppendAsync(User, id, offset, Request.Body, Request.ContentLength, ct);
        return result.Success ? NoContent() : UploadProblem(result.Error);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        var result = await files.CompleteAsync(User, id, ct);
        return result.Success ? Ok(result.Data) : UploadProblem(result.Error);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var result = await files.CancelAsync(User, id, ct);
        return result.Success ? NoContent() : UploadProblem(result.Error);
    }

    private ObjectResult UploadProblem(ServiceError error) => Problem(
        statusCode: error.Code switch
        {
            ErrorKeys.File.Validation => StatusCodes.Status400BadRequest,
            ErrorKeys.File.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKeys.File.NotFound => StatusCodes.Status404NotFound,
            ErrorKeys.File.Conflict => StatusCodes.Status409Conflict,
            ErrorKeys.File.Busy => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status500InternalServerError
        }, detail: localizer[error.Code, error.Args]);
}
