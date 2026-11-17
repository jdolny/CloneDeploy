using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController]
[Route("api/agent/v1/policies")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentPoliciesController(AgentPolicyExecutionService service) : ControllerBase
{
    private const int PolicyCommandRequestBytes = 4096;
    private const int PolicyResultRequestBytes = 100000;

    private int DeviceId => int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);

    [HttpGet("user")]
    public async Task<IActionResult> ForUser([FromQuery] string sid, [FromQuery] bool selfService, CancellationToken ct)
    {
        var result = await service.GetForUserAsync(DeviceId, sid, selfService, ct);
        return result.Success ? new JsonResult(result.Data, new JsonSerializerOptions()) : this.ServiceError(result);
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var result = await service.GetAsync(DeviceId, ct);
        return result.Success ? new JsonResult(result.Data, new JsonSerializerOptions()) : this.ServiceError(result);
    }

    [HttpPost("runs")]
    [RequestSizeLimit(PolicyCommandRequestBytes)]
    public async Task<IActionResult> Start(AgentPolicyStart request, CancellationToken ct) =>
        this.ToHttp(await service.StartAsync(DeviceId, request, ct));

    [HttpPost("setup-readiness"), RequestSizeLimit(PolicyCommandRequestBytes)]
    public async Task<IActionResult> SetupReadiness(AgentSetupReadiness request, CancellationToken ct) =>
        this.ToHttp(await service.ReportSetupReadinessAsync(DeviceId, request, ct));

    [HttpPost("results")]
    [RequestSizeLimit(PolicyResultRequestBytes)]
    public async Task<IActionResult> Complete(AgentPolicyResult request, CancellationToken ct) =>
        this.ToHttp(await service.CompleteAsync(DeviceId, request, ct));

    [HttpGet("runs/{runId:guid}/windows-updates/{actionGuid:guid}")]
    public async Task<IActionResult> WindowsUpdates(Guid runId, Guid actionGuid, CancellationToken ct) =>
        this.ToHttp(await service.WindowsUpdatesAsync(DeviceId, runId, actionGuid, ct));

    [HttpGet("runs/{runId:guid}/managed-updates")]
    public async Task<IActionResult> ManagedUpdates(Guid runId, CancellationToken ct) =>
        this.ToHttp(await service.ManagedUpdatesAsync(DeviceId, runId, ct));

    [HttpGet("runs/{runId:guid}/credentials/{credentialId:int}")]
    public async Task<IActionResult> Credential(Guid runId, int credentialId, CancellationToken ct) =>
        this.ToHttp(await service.CredentialAsync(DeviceId, runId, credentialId, ct));

    [HttpPost("runs/{runId:guid}/domain-join/{actionGuid:guid}")]
    [RequestSizeLimit(PolicyCommandRequestBytes)]
    public async Task<IActionResult> DomainJoin(Guid runId, Guid actionGuid, AgentDomainJoinRequest request, CancellationToken ct) =>
        this.ToHttp(await service.DomainJoinAsync(DeviceId, runId, actionGuid, request, ct));

    [HttpPost("runs/{runId:guid}/download-slot")]
    public async Task<IActionResult> AcquireDownloadSlot(Guid runId, CancellationToken ct) =>
        this.ToHttp(await service.AcquireDownloadSlotAsync(DeviceId, runId, ct));

    [HttpDelete("download-slots/{leaseId:guid}")]
    public async Task<IActionResult> ReleaseDownloadSlot(Guid leaseId, CancellationToken ct) =>
        this.ToHttp(await service.ReleaseDownloadSlotAsync(DeviceId, leaseId, ct));

    [HttpGet("runs/{runId:guid}/files/{fileId:int}")]
    public async Task<IActionResult> Download(Guid runId, int fileId, CancellationToken ct)
    {
        var result = await service.DownloadAsync(DeviceId, runId, fileId, async (stream, token) =>
        {
            if (stream is SiteDownloadStream) return;
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = stream.Length;
            await stream.CopyToAsync(Response.Body, token);
        }, ct);
        if (result.Success) return new EmptyResult();
        if (Response.HasStarted) { HttpContext.Abort(); return new EmptyResult(); }
        return this.ServiceError(result);
    }
}
