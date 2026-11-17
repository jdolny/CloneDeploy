using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

internal static class DeploymentTaskTracking
{
    // Caller owns the device advisory lock and transaction.
    public static async Task<ActiveImagingTask> SyncAsync(CloneDeployDbContext db, ImagingDeployment deployment, CancellationToken ct = default)
    {
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.DeviceId == deployment.DeviceId, ct);
        if (task is null) { task = new ActiveImagingTask { DeviceId = deployment.DeviceId }; db.Add(task); }
        if (task.DeploymentId != deployment.Id)
        {
            if (task.UploadId != null && task.Status < ImagingStatus.Succeeded) throw new InvalidOperationException("This device has an active capture.");
            task.UploadId = null; task.DeploymentId = deployment.Id; task.Type = "Deploy";
            task.AppUserId = null; task.Phase = 0; task.ProgressPercent = null; task.BytesTransferred = 0;
            task.Rate = null; task.StatusMessage = null; task.ErrorMessage = null; task.FinishedAtUtc = null;
            task.StartedAtUtc = deployment.CreatedAtUtc;
            task.ImageName = await db.ImageVersions.Where(x => x.Id == deployment.ImageVersionId).Select(x => x.Name).SingleAsync(ct);
        }
        task.LastUpdatedAtUtc = DateTime.UtcNow;
        if (deployment.CancelledAtUtc != null) { task.Status = ImagingStatus.Cancelled; task.FinishedAtUtc = deployment.CancelledAtUtc; task.ErrorMessage = null; }
        else if (deployment.ImagerFailedAtUtc != null) { task.Status = ImagingStatus.Failed; task.FinishedAtUtc = deployment.ImagerFailedAtUtc; task.ErrorMessage = "Deployment failed. Cancel this task before preparing a new deployment."; }
        else if (deployment.SetupCompletedAtUtc != null)
        {
            task.Status = ImagingStatus.Succeeded;
            task.FinishedAtUtc = deployment.SetupCompletedAtUtc;
            task.Phase = 4;
            task.ProgressPercent = 100;
            task.StatusMessage = deployment.SetupReadinessMessage ?? "Imaging and post-deployment setup completed.";
            task.ErrorMessage = null;
            task.Rate = null;
        }
        else if (deployment.ImagingCompletedAtUtc != null)
        {
            var steps = await db.Set<ImagingDeploymentPolicy>().Where(x => x.ImagingDeploymentId == deployment.Id)
                .OrderBy(x => x.Position).ToListAsync(ct);
            var next = steps.FirstOrDefault(x => x.FinishedAtUtc == null || x.Result == (int)PolicyRunResult.Failed);
            task.Status = ImagingStatus.Imaging;
            task.FinishedAtUtc = null;
            task.Rate = null;
            task.ErrorMessage = null;
            task.Phase = deployment.WindowsSetupReadyAtUtc is null ? 3 : 4;
            task.ProgressPercent = null;
            if (next?.Result == (int)PolicyRunResult.Failed)
            {
                task.Phase = 4;
                task.Status = ImagingStatus.Failed;
                task.ErrorMessage = await db.Set<AgentPolicyRun>().Where(x => x.Id == next.AgentPolicyRunId)
                    .Select(x => x.Detail).SingleOrDefaultAsync(ct) ?? "Post-deployment policy failed. Review before retrying or skipping.";
            }
            if (next is not null && (task.Phase == 4 || next.AgentPolicyRunId != null))
            {
                var snapshot = System.Text.Json.JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(next.SnapshotJson)!;
                var state = next.Result == (int)PolicyRunResult.Failed ? "Failed or interrupted" : next.AgentPolicyRunId is null ? "Ready" : "Running";
                task.StatusMessage = $"{state}: policy {steps.IndexOf(next) + 1} of {steps.Count} — {snapshot.Policy.Name}. Regular policies wait until setup completes.";
                task.ProgressPercent = 100d * steps.Count(x => x.FinishedAtUtc != null && x.Result != (int)PolicyRunResult.Failed) / Math.Max(1, steps.Count);
            }
            else
                task.StatusMessage = deployment.SetupReadinessMessage ?? "Waiting for Windows setup to finish and the agent to report readiness.";
        }
        else { task.Status = deployment.ImagerStartedAtUtc != null ? ImagingStatus.Imaging : deployment.ImagerClaimId == null ? ImagingStatus.TaskCreated : task.Status == ImagingStatus.InImagingQueue ? ImagingStatus.InImagingQueue : ImagingStatus.CheckedIn; task.FinishedAtUtc = null; }
        if (task.Status != ImagingStatus.InImagingQueue) task.QueuePosition = 0;
        return task;
    }
}
