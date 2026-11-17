using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

/// <summary>Provides only the authenticated device's pending boot plan and verified WinPE artifacts.</summary>
[ApiController, Route("api/agent/v1/imaging-boot"), Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
public sealed class AgentImagingBootController(ICloneDeployDbFactory factory, WinPeDownloadService downloads) : ControllerBase
{
    private const int BootPlanLifetimeHours = 2;
    private const int MaxProgressDetailLength = 2000;
    private const int DeviceImagingLockNamespace = 682947233;

    private int DeviceId => int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
    private static IQueryable<ImagingDeployment> Pending(CloneDeployDbContext db, int deviceId, Guid id) => db.Set<ImagingDeployment>()
        .Where(d => d.Id == id && d.DeviceId == deviceId && d.AgentBootPlanJson != null && d.CancelledAtUtc == null &&
            d.ImagerStartedAtUtc == null && d.ImagerFailedAtUtc == null && d.SetupCompletedAtUtc == null &&
            d.CreatedAtUtc > DateTime.UtcNow.AddHours(-BootPlanLifetimeHours) && db.Devices.Any(x => x.Id == deviceId && x.ArchivedAtUtc == null));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Plan(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var json = await Pending(db, DeviceId, id).Select(d => d.AgentBootPlanJson).SingleOrDefaultAsync(ct);
        return json is null ? NotFound() : Content(json, "application/json");
    }
    [HttpGet("{id:guid}/files/{name}")]
    public async Task<IActionResult> File(Guid id, string name, CancellationToken ct)
    {
        if (name is not ("boot.wim" or "boot.sdi")) return NotFound();
        await using var db = await factory.CreateContextAsync();
        var json = await Pending(db, DeviceId, id).Select(d => d.AgentBootPlanJson).SingleOrDefaultAsync(ct);
        if (json is null) return NotFound();
        var plan = JsonSerializer.Deserialize<AgentImagingReview>(json)!;
        var result = await downloads.DownloadAsync(plan.BuildId, plan.SourceFile(name), ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true) : NotFound();
    }
    [HttpPost("{id:guid}/progress")]
    public async Task<IActionResult> Progress(Guid id, AgentBootProgress progress, CancellationToken ct)
    {
        if (progress.Stage is not ("Preparing" or "Restarting" or "Failed") || string.IsNullOrWhiteSpace(progress.Detail) || progress.Detail.Length > MaxProgressDetailLength) return BadRequest();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({DeviceImagingLockNamespace}, {DeviceId})", ct);
        var deployment = await Pending(db, DeviceId, id).SingleOrDefaultAsync(ct);
        if (deployment is null) return Conflict();
        if (progress.Stage == "Failed") { deployment.ImagerFailedAtUtc = DateTime.UtcNow; deployment.AgentBootReady = false; }
        else if (progress.Stage == "Restarting") deployment.AgentBootReady = true;
        var task = await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        task.StatusMessage = progress.Detail;
        if (progress.Stage == "Failed") task.ErrorMessage = progress.Detail;
        if (progress.Stage == "Failed")
            db.DeviceImagingLogs.Add(new DeviceImagingLog { DeviceId = DeviceId, Type = "Agent", SubType = progress.Stage, Contents = progress.Detail });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return NoContent();
    }
}
