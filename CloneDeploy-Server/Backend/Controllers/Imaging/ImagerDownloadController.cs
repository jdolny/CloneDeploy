using CloneDeploy_Server.WinPe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, AllowAnonymous, Route("api/imaging/client")]
[EnableRateLimiting("ImagerDownload")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ImagerDownloadController(ImagerBundle bundle, ILogger<ImagerDownloadController> logger) : ControllerBase
{
    [HttpGet("win-{architecture:regex(^(x64|arm64)$)}.json")]
    public Task<IActionResult> Manifest(string architecture, CancellationToken ct) => Download(false, null, ct, architecture);
    [HttpGet("win-{architecture:regex(^(x64|arm64)$)}.zip")]
    public Task<IActionResult> Archive(string architecture, [FromQuery] string? sha256, CancellationToken ct) => Download(true, sha256, ct, architecture);

    private async Task<IActionResult> Download(bool archive, string? expectedHash, CancellationToken ct, string architecture)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Imager downloads require HTTPS.");
        try
        {
            var package = await bundle.ForArchitecture(architecture).OpenDownloadAsync(ct);
            if (archive && (expectedHash is null || string.Equals(expectedHash, package.Sha256, StringComparison.OrdinalIgnoreCase)))
                return File(package.Stream, "application/zip", $"CloneDeploy-Imager-win-{architecture}.zip");
            await using var stream = package.Stream;
            if (archive) return Conflict(new { message = "The Imager was updated. Fetch the manifest again." });
            return Ok(new { package.Version, package.Sha256, SizeBytes = stream.Length, Architecture = architecture });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Standard Imager bundle is unavailable");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "The standard Imager bundle is unavailable. Publish or refresh the server bundle.");
        }
    }
}
