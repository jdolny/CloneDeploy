using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Route("api/agent/v1/admin/image-prep")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentImagePrepController(AgentAdminSessions sessions, ICloneDeployDbFactory factory) : ControllerBase
{
    private const int MaxRequestBytes = 8192;

    private async Task<int?> AccessAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var deviceGuid)
            || !int.TryParse(Request.Headers["X-CloneDeploy-Windows-Session"], out var sessionId))
            return null;

        return await sessions.AuthorizeToolAsync(deviceGuid, new(
            Request.Headers["X-CloneDeploy-Admin-Token"].ToString(),
            Request.Headers["X-CloneDeploy-Windows-Sid"].ToString(), sessionId), "RunImagePrep", factory, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Profiles(CancellationToken ct)
    {
        var userId = await AccessAsync(ct);
        if (userId is null) return Unauthorized();

        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId.Value, Constants.Permissions.ImagePrepProfileView, ct)) return Forbid();
        var capture = await ImagingPermissions.HasAsync(db, userId.Value, Constants.Permissions.ImageCapture, ct);
        return Ok(await db.ImagePrepProfiles.AsNoTracking().Where(x => x.Enabled && (!x.RebootAndCapture || capture)).OrderBy(x => x.Name)
            .Select(x => new AgentImagePrepProfile(x.Id, x.Name, x.Description) { RebootAndCapture = x.RebootAndCapture, UninstallAgent = x.UninstallAgent }).ToArrayAsync(ct));
    }

    [HttpGet("images")]
    public async Task<IActionResult> Images([FromServices] ImagingUploadService uploads, CancellationToken ct)
    {
        var userId = await AccessAsync(ct);
        if (userId is null) return Unauthorized();
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId.Value, Constants.Permissions.ImageView, ct)) return Ok(Array.Empty<AgentCaptureImage>());
        return Ok(await uploads.ImagesAsync(ct));
    }

    [HttpPost("{id:int}/start"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Start(int id, CancellationToken ct)
    {
        var userId = await AccessAsync(ct);
        if (userId is null)
            return Unauthorized();

        await using var db = await factory.CreateContextAsync();
        var profile = await db.ImagePrepProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Enabled, ct);
        if (profile is null)
            return NotFound();

        if (!await ImagingPermissions.HasAsync(db, userId.Value, profile.RebootAndCapture ? Constants.Permissions.ImageCapture : Constants.Permissions.AgentAdminTools, ct)) return Forbid();
        if (profile.UninstallAgent && Request.Headers["X-CloneDeploy-ImagePrep-Uninstall"] != "1")
            return Conflict(new { message = "Update the agent before using an image prep profile that uninstalls it." });
        string? credentialTicket = null;
        if (profile.GeneratorOptions?.CredentialIds().Any() == true)
        {
            try { credentialTicket = await HttpContext.RequestServices.GetRequiredService<ImagePrepCredentialService>().CreateAsync(db, profile, ct); }
            catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        }
        AgentPreparedCapture? capture = null;
        if (profile.RebootAndCapture)
        {
            var selection = Request.HasJsonContentType()
                ? await Request.ReadFromJsonAsync<AgentImagePrepRequest>(ct) : null;
            if (selection is null) return BadRequest(new { message = "Update the agent and select a capture destination." });
            try
            {
                capture = await HttpContext.RequestServices.GetRequiredService<PreparedCaptureService>().PrepareAsync(
                    Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), userId.Value, selection, ct);
            }
            catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
            catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        }
        var userName = await db.AppUsers.Where(x => x.Id == userId).Select(x => x.UserName).SingleAsync(ct);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId.Value,
            UserName = userName ?? "Administrator",
            AuditType = AuditType.Update,
            ObjectType = nameof(ImagePrepProfile),
            ObjectId = id,
            ObjectName = profile.Name,
            ObjectJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                Action = "Image prep authorized",
                UploadId = capture?.UploadId,
                ImageId = capture?.Boot.ImageId,
                DeviceGuid = User.FindFirstValue(ClaimTypes.NameIdentifier)
            })
        });
        await db.SaveChangesAsync(ct);
        return Ok(profile.ToPlan() with { Capture = capture, SysprepCredentialTicket = credentialTicket });
    }
}
