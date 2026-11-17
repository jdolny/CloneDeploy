using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Called inside the catalog deletion transaction. Preserve history and stop work
// that still needs the deleted content; completed imaging/setup is left intact.
internal static class ImageDeletionPreparation
{
    private const string Reason = "Image content was deleted by a user.";

    internal static async Task CancelReferencesAsync(CloneDeployDbContext db, Guid[] versions)
    {
        var now = DateTime.UtcNow;
        var deployments = await db.Set<ImagingDeployment>().Where(x => versions.Contains(x.ImageVersionId)
            && x.ImagingCompletedAtUtc == null && x.CancelledAtUtc == null).Select(x => x.Id).ToArrayAsync();
        await db.Set<ImagingDeployment>().Where(x => deployments.Contains(x.Id) && x.ImagingCompletedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAtUtc, now)
                .SetProperty(x => x.ImagerClaimExpiresAtUtc, (DateTime?)null).SetProperty(x => x.SetupReadinessMessage, Reason));
        await db.ActiveImagingTasks.Where(x => x.DeploymentId != null && deployments.Contains(x.DeploymentId.Value)
                && x.Status < ImagingStatus.Succeeded)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ImagingStatus.Cancelled)
                .SetProperty(x => x.FinishedAtUtc, now).SetProperty(x => x.LastUpdatedAtUtc, now).SetProperty(x => x.StatusMessage, Reason));
        await db.Set<MulticastSession>().Where(x => versions.Contains(x.ImageVersionId) && x.State < MulticastState.Completed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, MulticastState.Cancelled)
                .SetProperty(x => x.FinishedAtUtc, now).SetProperty(x => x.Error, Reason));
        await db.Set<ImageReplica>().Where(x => versions.Contains(x.ImageVersionId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ImageReplicaState.Deleting)
                .SetProperty(x => x.RetryAtUtc, (DateTime?)null).SetProperty(x => x.UpdatedAtUtc, now));
    }

    internal static Task CancelUploadsAsync(CloneDeployDbContext db, Guid[] uploads) =>
        db.ActiveImagingTasks.Where(x => x.UploadId != null && uploads.Contains(x.UploadId.Value) && x.Status < ImagingStatus.Succeeded)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ImagingStatus.Cancelled)
                .SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow).SetProperty(x => x.LastUpdatedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.StatusMessage, Reason));
}
