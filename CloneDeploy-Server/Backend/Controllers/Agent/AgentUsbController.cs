using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Route("api/agent/v1/admin/usb")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentUsbController(AgentAdminSessions sessions, ICloneDeployDbFactory factory,
    WinPeArtifactStorage artifacts, WinPeDownloadService downloads) : ControllerBase
{
    private async Task<bool> AccessAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var device) ||
            !int.TryParse(Request.Headers["X-CloneDeploy-Windows-Session"], out var session)) return false;
        return await sessions.AuthorizeToolAsync(device, new(Request.Headers["X-CloneDeploy-Admin-Token"].ToString(),
            Request.Headers["X-CloneDeploy-Windows-Sid"].ToString(), session), "CreateBootableUsb", factory, ct) is not null;
    }

    [HttpGet("variants")]
    public async Task<IActionResult> Variants(CancellationToken ct)
    {
        if (!await AccessAsync(ct)) return Unauthorized();
        var architecture = Request.Query["architecture"].FirstOrDefault() ?? "x64";
        if (architecture is not ("x64" or "arm64")) return BadRequest("Unsupported architecture.");
        await using var db = await factory.CreateContextAsync();
        var builds = await db.WinPeBuilds.AsNoTracking().Include(x => x.Definition)
            .Where(x => !x.Definition.IsBase && x.StoredAtUtc != null && x.Definition.PublishedBuildId == x.Id).ToListAsync(ct);
        var result = new List<AgentUsbVariant>();
        foreach (var group in builds.GroupBy(x => x.DefinitionId))
        {
            foreach (var build in group.OrderByDescending(x => x.Definition.PublishedBuildId == x.Id).ThenByDescending(x => x.Number))
            {
                var status = artifacts.Status(build);
                var iso = status.Artifacts.FirstOrDefault(x => x.Name == $"winpe-{architecture}.iso");
                if (status.State != "Ready" || iso is null) continue;
                result.Add(new(build.Id, build.Definition.Name, build.Number, iso.Size, iso.Sha256) { Architecture = architecture });
                break;
            }
        }
        return Ok(result.OrderBy(x => x.Name));
    }

    [HttpGet("builds/{id:guid}/iso")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        if (!await AccessAsync(ct)) return Unauthorized();
        var architecture = Request.Query["architecture"].FirstOrDefault() ?? "x64";
        if (architecture is not ("x64" or "arm64")) return BadRequest("Unsupported architecture.");
        await using var db = await factory.CreateContextAsync();
        if (!await db.WinPeBuilds.AnyAsync(x => x.Id == id && !x.Definition.IsBase && x.StoredAtUtc != null, ct)) return NotFound();
        var result = await downloads.DownloadAsync(id, $"winpe-{architecture}.iso", ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", result.Data.Name, enableRangeProcessing: true)
            : Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "The WinPE ISO is unavailable. Check build storage.");
    }
}
