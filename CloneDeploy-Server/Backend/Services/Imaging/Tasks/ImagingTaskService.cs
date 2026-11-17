using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ImagingProgressDto(int Phase, double? Percent, double? NetworkBytesPerSecond = null)
{
    public string? StatusMessage { get; init; }
}
public sealed record ImagingTaskRowDto(int DeviceId, string DeviceName, string? ImageName, string? Type,
    ImagingStatus Status, int Phase, double? Percent, long BytesTransferred, DateTime StartedAtUtc,
    DateTime LastUpdatedAtUtc, DateTime? FinishedAtUtc, string? ErrorMessage, Guid? UploadId, string? Rate = null)
{
    public int? RequestedByUserId { get; init; }
    public string RequestedBy { get; init; } = "System / unknown";
    public bool CanManage { get; init; }
    public string? StatusMessage { get; init; }
    public Guid? DeploymentId { get; init; }
    public int QueuePosition { get; init; }
    public bool SetupPending { get; init; }
    public List<ImagingSetupStepDto> SetupSteps { get; init; } = [];
}

public sealed record ImagingSetupStepDto(Guid Id, string Hash, string Name, int Position, string Status, string? Detail, bool CanRecover);

public sealed class ImagingTaskService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ImagingUploadService uploads, ILogger<ImagingTaskService> logger)
{
    public Task<ServiceResult<List<ImagingTaskRowDto>>> GetAsync(CancellationToken ct = default) => GetAsync(false, ct);

    public Task<ServiceResult<List<ImagingTaskRowDto>>> GetAsync(bool allTasks, CancellationToken ct = default) => ServiceOperations.RunAsync(async () =>
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization);
        if (access is null)
            return ServiceResult<List<ImagingTaskRowDto>>.Fail(ServiceErrorCodes.Forbidden);
        await using var db = await factory.CreateContextAsync();
        if (allTasks && !access.ViewAll) return ServiceResult<List<ImagingTaskRowDto>>.Fail(ServiceErrorCodes.Forbidden);
        var rows = await db.ActiveImagingTasks.AsNoTracking().Where(x => allTasks || x.AppUserId == access.UserId)
            .OrderByDescending(x => x.FinishedAtUtc == null).ThenByDescending(x => x.LastUpdatedAtUtc)
            .Select(x => new ImagingTaskRowDto(x.DeviceId, x.Device!.Name, x.ImageName, x.Type, x.Status,
                x.Phase, x.ProgressPercent, x.BytesTransferred, x.StartedAtUtc, x.LastUpdatedAtUtc, x.FinishedAtUtc, x.ErrorMessage, x.UploadId, x.Rate) { RequestedByUserId = x.AppUserId, RequestedBy = x.AppUser == null ? "System / unknown" : x.AppUser.UserName ?? "Unknown user", CanManage = access.ManageAll || x.AppUserId == access.UserId, DeploymentId = x.DeploymentId, QueuePosition = x.QueuePosition, StatusMessage = x.StatusMessage })
            .ToListAsync(ct);
        var canRecover = (await authorization.AuthorizeAsync(access.User, Constants.Permissions.DeviceDeployImage)).Succeeded ||
            (await authorization.AuthorizeAsync(access.User, Constants.Permissions.DeviceDeployImageThroughAgent)).Succeeded ||
            (await authorization.AuthorizeAsync(access.User, Constants.Permissions.DeviceGroupDeployImage)).Succeeded ||
            (await authorization.AuthorizeAsync(access.User, Constants.Permissions.DeviceGroupDeployMulticast)).Succeeded;
        var ids = rows.Where(x => x.DeploymentId != null).Select(x => x.DeploymentId!.Value).ToList();
        var deployments = await db.Set<ImagingDeployment>().AsNoTracking().Include(x => x.Policies)
            .Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var runIds = deployments.Values.SelectMany(x => x.Policies).Where(x => x.AgentPolicyRunId != null)
            .Select(x => x.AgentPolicyRunId!.Value).ToList();
        var runs = await db.Set<AgentPolicyRun>().AsNoTracking().Where(x => runIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Detail }).ToDictionaryAsync(x => x.Id, ct);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].DeploymentId is not { } id || !deployments.TryGetValue(id, out var deployment)) continue;
            var pending = deployment.ImagingCompletedAtUtc != null && deployment.SetupCompletedAtUtc == null && deployment.CancelledAtUtc == null;
            var steps = deployment.Policies.OrderBy(x => x.Position).ToList();
            var next = steps.FirstOrDefault(x => x.FinishedAtUtc == null || x.Result == 1);
            // Existing deployments may still have the old image-only success status until the agent reports again.
            if (pending && rows[i].Phase < 3)
                rows[i] = rows[i] with
                {
                    Status = next?.Result == 1 ? ImagingStatus.Failed : ImagingStatus.Imaging,
                    Phase = next?.Result == 1 ? 4 : 3,
                    FinishedAtUtc = null, Percent = null, Rate = null,
                    StatusMessage = next?.Result == 1 ? "Post-deployment policy failed; review the setup steps."
                        : "Waiting for Windows setup to finish and the agent to report readiness."
                };
            rows[i] = rows[i] with
            {
                SetupPending = pending,
                SetupSteps = steps.Select((step, index) =>
                {
                    var run = step.AgentPolicyRunId is { } runId ? runs.GetValueOrDefault(runId) : null;
                    var snapshot = System.Text.Json.JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(step.SnapshotJson)!;
                    var status = step.Result switch { 0 => "Completed", 1 => "Failed / interrupted", 2 => "Skipped", 3 => "Not applicable",
                        _ => deployment.CancelledAtUtc != null ? "Cancelled" : step.AgentPolicyRunId != null ? "Running" : "Pending" };
                    return new ImagingSetupStepDto(step.Id, step.Hash, snapshot.Policy.Name, index + 1, status,
                        step.Result == 2 ? deployment.SetupReadinessMessage ?? "Skipped; see deployment audit for administrator recovery actions." : run?.Detail,
                        rows[i].CanManage && canRecover && pending && next?.Id == step.Id && step.FinishedAtUtc != null && step.Result == 1);
                }).ToList()
            };
        }
        return ServiceResult<List<ImagingTaskRowDto>>.Ok(rows);
    }, logger);

    public Task<ServiceResult> CancelAsync(Guid uploadId, CancellationToken ct = default) => ServiceOperations.RunAsync(async () =>
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization);
        if (access is null)
            return ServiceResult.Fail(ServiceErrorCodes.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var task = await db.ActiveImagingTasks.AsNoTracking().SingleOrDefaultAsync(x => x.UploadId == uploadId, ct);
        if (task is null) return ServiceResult.Fail(ServiceErrorCodes.NotFound);
        if (!access.CanManage(task.AppUserId)) return ServiceResult.Fail(ServiceErrorCodes.Forbidden);
        if (task.Status >= ImagingStatus.Succeeded) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImagingTaskService.ThisTaskHasAlreadyFinished));
        await uploads.AbortAsync(uploadId, task.AppUserId ?? throw new InvalidOperationException("Upload owner is missing."), ct);
        return ServiceResult.Ok();
    }, logger);
    public async Task<ServiceResult> CancelDeploymentAsync(Guid id, int deviceId)
    {
        var service = new ImagingDeploymentService(factory, authentication, authorization);
        return await service.CancelAsync(id, deviceId);
    }

    public Task<ServiceResult> RecoverPolicyAsync(Guid id, int deviceId, Guid stepId, string hash, bool skip) =>
        ServiceOperations.RunAsync(() => new ImagingDeploymentService(factory, authentication, authorization)
            .RecoverPolicyAsync(id, deviceId, stepId, hash, skip), logger);
}
