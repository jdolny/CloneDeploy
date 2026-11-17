using CloneDeploy_Server.Backend.Services;
using CloneDeploy.SiteServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.SiteServices;
// These routes authenticate the enrolled TLS certificate in Site(), not a web UI session.
[ApiController, AllowAnonymous, Route("api/site-services/windows")]
public sealed class WindowsWorkerController(SiteServiceEnrollmentService enrollment, WindowsWorkQueue jobs) : ControllerBase
{
    private const int ClaimRequestBytes = 1024;
    private const int RenewRequestBytes = 4096;
    private const int CompleteRequestBytes = 262144;
    private const int HealthRequestBytes = 32768;

    private async Task<Guid> Site(CancellationToken ct)
    {
        if (!Request.IsHttps) throw new UnauthorizedAccessException();
        var certificate = await HttpContext.Connection.GetClientCertificateAsync(ct) ?? throw new UnauthorizedAccessException();
        return await enrollment.AuthorizeWorkerAsync(certificate, ct);
    }
    [HttpPost("claim"), RequestSizeLimit(ClaimRequestBytes)]
    public Task<IActionResult> Claim(CancellationToken ct) => Execute(async () =>
    { var offer = await jobs.ClaimAsync(await Site(ct), ct); return offer is null ? NoContent() : Ok(offer); });
    [HttpPost("{id:guid}/{lease:guid}/renew"), RequestSizeLimit(RenewRequestBytes)]
    public Task<IActionResult> Renew(Guid id, Guid lease, WindowsWorkProgress progress, CancellationToken ct) => Execute(async () =>
    { await jobs.RenewAsync(await Site(ct), id, lease, progress.Message, ct); return NoContent(); });
    [HttpGet("{id:guid}/{lease:guid}/input/{name}")]
    public Task<IActionResult> Input(Guid id, Guid lease, string name, CancellationToken ct) => Execute(async () =>
        File(await jobs.InputAsync(await Site(ct), id, lease, name, ct), "application/octet-stream"));
    [HttpPut("{id:guid}/{lease:guid}/output/{name}"), DisableRequestSizeLimit]
    public Task<IActionResult> Output(Guid id, Guid lease, string name, [FromQuery] long length, [FromQuery] string sha256, CancellationToken ct) => Execute(async () =>
    { await jobs.UploadAsync(await Site(ct), id, lease, new(name, length, sha256), Request.Body, ct); return NoContent(); });
    [HttpPost("{id:guid}/{lease:guid}/complete"), RequestSizeLimit(CompleteRequestBytes)]
    public Task<IActionResult> Complete(Guid id, Guid lease, WindowsWorkCompletion completion, CancellationToken ct) => Execute(async () =>
    { await jobs.CompleteAsync(await Site(ct), id, lease, completion, ct); return NoContent(); });
    [HttpPost("health"), RequestSizeLimit(HealthRequestBytes)]
    public Task<IActionResult> Health(WindowsWorkerHealth health, [FromServices] WindowsWinPeBridge bridge, CancellationToken ct) => Execute(async () =>
    { await bridge.HealthAsync(await Site(ct), health, ct); return NoContent(); });
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> operation)
    {
        Response.Headers.CacheControl = "no-store";
        try { return await operation(); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or System.Text.Json.JsonException) { return BadRequest("Invalid Windows work request."); }
    }
}
