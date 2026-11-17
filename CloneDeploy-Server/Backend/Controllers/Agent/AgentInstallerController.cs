using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Authorize(Policy = Constants.Permissions.AdminOnly)]
[Route("api/ui/administration/agent/general/installer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentInstallerController(AgentInstallerService installers, ILogger<AgentInstallerController> logger) : ControllerBase
{
    [HttpGet("macos/current")]
    public async Task<IActionResult> DownloadMac([FromServices] MacAgentReleaseService releases, CancellationToken ct)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Agent installer downloads contain enrollment credentials and require HTTPS. Open the management site over HTTPS and try the download again.");
        var file = await releases.OpenInstallerAsync(ct);
        return file is null ? Problem(statusCode: StatusCodes.Status409Conflict, detail: "Prepare the agent release again to generate the Mac installation bundle.") : File(file, "application/zip", "CloneDeploy-Agent-macOS-arm64.zip");
    }
    [HttpGet("current")]
    public async Task<IActionResult> Download([FromServices] AgentReleaseService releases, CancellationToken ct, [FromQuery] string architecture = "x64")
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Agent installer downloads contain enrollment credentials and require HTTPS. Open the management site over HTTPS and try the download again.");
        if (architecture is not ("x64" or "arm64")) return BadRequest();
        var file = await releases.OpenBundleAsync(ct, architecture);
        return file is null ? Problem(statusCode: 409, detail: "Prepare the agent release again to generate a deployment bundle.") : File(file, "application/zip", $"CloneDeploy-Agent-{architecture}.zip");
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(CancellationToken ct, [FromQuery] string architecture = "x64")
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Installer downloads require HTTPS.");
        try { return File(await installers.GenerateAsync(User, ct, architecture), "application/zip", $"CloneDeploy-Agent-{architecture}.zip"); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidDataException ex) { return Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Native MSI exceptions can carry SQL data; never log exception contents here.
            logger.LogError("Agent installer generation failed ({ExceptionType})", ex.GetType().Name);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "The agent installer could not be generated. Check the template and server configuration.");
        }
    }
}
