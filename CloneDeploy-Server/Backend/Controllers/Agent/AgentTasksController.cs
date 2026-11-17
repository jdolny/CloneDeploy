using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

[ApiController]
[Route("api/agent/v1/tasks")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentTasksController(AgentTaskExecutionService service) : ControllerBase
{
    private const int TaskCommandRequestBytes = 4096;
    private const int TaskResultRequestBytes = 32768;

    private int DeviceId => int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
    private Guid DeviceGuid => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid? LeaseToken => Guid.TryParse(Request.Headers["X-Task-Lease"], out var token) ? token : null;

    [HttpPost("claim")]
    [RequestSizeLimit(TaskCommandRequestBytes)]
    public async Task<IActionResult> Claim(AgentTaskClaim request, CancellationToken ct) =>
        this.ToHttp(await service.ClaimAsync(DeviceId, DeviceGuid, LeaseToken, request, ct));

    [HttpPost("{taskId:guid}/start")]
    [RequestSizeLimit(TaskCommandRequestBytes)]
    public async Task<IActionResult> Start(Guid taskId, AgentTaskLease request, CancellationToken ct) =>
        this.ToHttp(await service.StartAsync(DeviceId, DeviceGuid, LeaseToken, taskId, request, ct));

    [HttpPost("{taskId:guid}/heartbeat")]
    [RequestSizeLimit(TaskCommandRequestBytes)]
    public async Task<IActionResult> Heartbeat(Guid taskId, AgentTaskLease request, CancellationToken ct) =>
        this.ToHttp(await service.HeartbeatAsync(DeviceId, DeviceGuid, LeaseToken, taskId, request, ct));

    [HttpPost("{taskId:guid}/result")]
    [RequestSizeLimit(TaskResultRequestBytes)]
    public async Task<IActionResult> Result(Guid taskId, AgentTaskResult request, CancellationToken ct) =>
        this.ToHttp(await service.ResultAsync(DeviceId, DeviceGuid, LeaseToken, taskId, request, ct));

    [HttpGet("{runId:guid}/windows-updates/{actionGuid:guid}")]
    public async Task<IActionResult> WindowsUpdates(Guid runId, Guid actionGuid, CancellationToken ct) =>
        this.ToHttp(await service.WindowsUpdatesAsync(DeviceId, DeviceGuid, LeaseToken, runId, actionGuid, ct));

    [HttpGet("{runId:guid}/managed-updates")]
    public async Task<IActionResult> ManagedUpdates(Guid runId, CancellationToken ct) =>
        this.ToHttp(await service.ManagedUpdatesAsync(DeviceId, DeviceGuid, LeaseToken, runId, ct));

    [HttpGet("{runId:guid}/credentials/{credentialId:int}")]
    public async Task<IActionResult> Credential(Guid runId, int credentialId, CancellationToken ct) =>
        this.ToHttp(await service.CredentialAsync(DeviceId, DeviceGuid, LeaseToken, runId, credentialId, ct));

    [HttpPost("{runId:guid}/domain-join/{actionGuid:guid}")]
    [RequestSizeLimit(TaskCommandRequestBytes)]
    public async Task<IActionResult> DomainJoin(Guid runId, Guid actionGuid, AgentDomainJoinRequest request, CancellationToken ct) =>
        this.ToHttp(await service.DomainJoinAsync(DeviceId, DeviceGuid, LeaseToken, runId, actionGuid, request, ct));

    [HttpPost("{runId:guid}/download-slot")]
    public async Task<IActionResult> AcquireDownloadSlot(Guid runId, CancellationToken ct) =>
        this.ToHttp(await service.AcquireDownloadSlotAsync(DeviceId, DeviceGuid, LeaseToken, runId, ct));

    [HttpDelete("download-slots/{leaseId:guid}")]
    public async Task<IActionResult> ReleaseDownloadSlot(Guid leaseId, CancellationToken ct) =>
        this.ToHttp(await service.ReleaseDownloadSlotAsync(DeviceId, DeviceGuid, LeaseToken, leaseId, ct));

    [HttpGet("{runId:guid}/files/{fileId:int}")]
    public async Task<IActionResult> Download(Guid runId, int fileId, CancellationToken ct)
    {
        var result = await service.DownloadAsync(DeviceId, DeviceGuid, LeaseToken, runId, fileId, async (stream, token) =>
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
