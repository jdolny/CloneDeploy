using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy_Server.Backend.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController, Route("api/agent/v1/admin/driver-packs")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentDriverPacksController(AgentAdminSessions sessions, ICloneDeployDbFactory factory,
    ChunkedUploadStore uploads, AgentDriverPackImport importer) : ControllerBase
{
    private const int DriverPackRequestBytes = 4096;

    private Guid DeviceGuid => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private async Task<(string Owner, int UserId)?> AccessAsync(CancellationToken ct)
    {
        var token = Request.Headers["X-CloneDeploy-Admin-Token"].ToString();
        var sid = Request.Headers["X-CloneDeploy-Windows-Sid"].ToString();
        if (!int.TryParse(Request.Headers["X-CloneDeploy-Windows-Session"], out var sessionId)) return null;
        var userId = await sessions.AuthorizeToolAsync(DeviceGuid, new(token, sid, sessionId), "CreateDriverPack", factory, ct);
        return userId.HasValue ? ("agent-driver:" + DeviceGuid + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), userId.Value) : null;
    }
    private async Task<IActionResult> UploadAsync(Func<string, Task<IActionResult>> operation, CancellationToken ct)
    {
        var access = await AccessAsync(ct);
        if (access is null) return Unauthorized();
        try { return await operation(access.Value.Owner); }
        catch (UploadException ex) { return Problem(statusCode: ex.StatusCode, detail: ex.Message); }
    }
    [HttpPost("uploads"), RequestSizeLimit(DriverPackRequestBytes)]
    public Task<IActionResult> Start(AgentDriverUploadRequest request, CancellationToken ct) => UploadAsync(async owner =>
        Ok(await uploads.StartAsync(owner, new(request.FileName, request.Size), ct)), ct);
    [HttpPut("uploads/{id:guid}/chunks"), RequestSizeLimit(ChunkedUploadStore.ChunkSize + UploadRequestLimits.ChunkOverheadBytes)]
    public Task<IActionResult> Chunk(Guid id, [FromQuery] long offset, CancellationToken ct) => UploadAsync(async owner =>
        { await uploads.AppendAsync(owner, id, offset, Request.Body, Request.ContentLength, ct); return NoContent(); }, ct);
    [HttpPost("uploads/{id:guid}/complete")]
    public Task<IActionResult> Complete(Guid id, CancellationToken ct) => UploadAsync(async owner =>
        Ok(await uploads.CompleteAsync(owner, id, ct)), ct);
    [HttpDelete("uploads/{id:guid}")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => UploadAsync(async owner =>
        { await uploads.CancelAsync(owner, id, ct); return NoContent(); }, ct);
    [HttpPost, RequestSizeLimit(DriverPackRequestBytes)]
    public async Task<IActionResult> Create(AgentDriverPackCreateRequest request, CancellationToken ct)
    {
        var access = await AccessAsync(ct);
        if (access is null) return Unauthorized();
        var result = await importer.CreateAsync(access.Value.Owner, access.Value.UserId, DeviceGuid, request, ct);
        return result.Success ? Ok(result.Data) : Problem(statusCode: result.Error.Code.EndsWith("Validation") ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError,
            detail: result.Error.Code.EndsWith("Validation") ? string.Join(" ", result.Error.Args) : "The driver pack could not be published.");
    }
}
