using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Route("api/agent/v1/prepared-capture/{id:guid}")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentPreparedCaptureController(PreparedCaptureService captures, ImagingUploadService uploads,
    WinPeDownloadService downloads) : ControllerBase
{
    private Guid Device => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var row = await captures.ForAgentAsync(id, Device, ct);
        return row is null ? NotFound() : Ok(JsonSerializer.Deserialize<AgentImagingReview>(row.CaptureBootPlanJson!));
    }

    [HttpGet("files/{name}")]
    public async Task<IActionResult> File(Guid id, string name, CancellationToken ct)
    {
        if (name is not ("boot.wim" or "boot.sdi")) return NotFound();
        var row = await captures.ForAgentAsync(id, Device, ct);
        if (row is null) return NotFound();
        var plan = JsonSerializer.Deserialize<AgentImagingReview>(row.CaptureBootPlanJson!)!;
        var result = await downloads.DownloadAsync(plan.BuildId, plan.SourceFile(name), ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true) : NotFound();
    }

    [HttpPost("ready")]
    public async Task<IActionResult> Ready(Guid id, CancellationToken ct)
    {
        if (await captures.ForAgentAsync(id, Device, ct) is null) return NotFound();
        try { await captures.ReadyAsync(id, ct); return NoContent(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpDelete]
    public async Task<IActionResult> Abort(Guid id, CancellationToken ct)
    {
        var row = await captures.ForAgentAsync(id, Device, ct);
        if (row is not null) await uploads.AbortAsync(id, row.UserId, ct, "Failed");
        return NoContent();
    }
}
