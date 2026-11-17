using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class DashboardAttentionQueries
{
    internal static async Task PopulateAsync(CloneDeployDbContext db, DashboardSnapshotDto result,
        IReadOnlyList<StorageMountStatusDto> storage, AgentReleaseReadiness? release,
        IReadOnlyList<CertificateExpiryNotice>? certificateNotices = null)
    {
        var notices = result.Attention;
        void Add(string key, string href, DashboardAttentionSeverity severity, int count)
        {
            if (count > 0) notices.Add(new(key, $"Dashboard.Attention.{key}", $"Dashboard.Attention.{key}.Hint", href, severity, count));
        }
        if (result.CanApprove)
        {
            Add("Enrollment", "/devices/approval-and-reset", DashboardAttentionSeverity.Information, result.PendingApprovals);
            Add("Reset", "/devices/approval-and-reset", DashboardAttentionSeverity.Warning,
                await db.AgentProvisioningRequests.CountAsync(x => x.State == AgentProvisioningState.PendingReset));
        }
        if (result.CanViewTasks)
        {
        Add("UnknownDeployments", "/tasks/deployments?status=Unknown", DashboardAttentionSeverity.Warning,
            await db.DeviceTasks.CountAsync(x => (result.CanViewAllTasks || x.Deployment.RequestedByUserId == result.TaskUserId) && x.Status == DeviceTaskStatus.Unknown));
        var yesterday = result.AsOfUtc.AddDays(-1);
        Add("Imaging", "/tasks/imaging", DashboardAttentionSeverity.Error,
            await db.ActiveImagingTasks.CountAsync(x => (result.CanViewAllTasks || x.AppUserId == result.TaskUserId) && x.Status == ImagingStatus.Failed && x.Device!.ArchivedAtUtc == null &&
                (x.DeploymentId == null ? x.LastUpdatedAtUtc >= yesterday :
                    db.Set<ImagingDeployment>().Any(d => d.Id == x.DeploymentId && d.CancelledAtUtc == null && d.SetupCompletedAtUtc == null))));

        }
        if (result.CanViewUpdates)
        Add("WindowsUpdates", "/reports/windowsupdates?approval=Unapproved", DashboardAttentionSeverity.Warning,
            await db.WindowsUpdates.CountAsync(x => x.RevisionNumber >= 0 && x.UpdateId != null &&
                (x.ApprovedRevisionNumber == null || x.ApprovedRevisionNumber != x.RevisionNumber) &&
                db.DeviceWindowsUpdates.Any(d => d.WindowsUpdateId == x.Id && !d.IsInstalled && d.IsHidden != true &&
                                                d.Device!.ArchivedAtUtc == null)));


        if (!result.IsAdmin) return;

        foreach (var notice in certificateNotices ?? []) notices.Add(notice.DashboardNotice());

        var now = result.AsOfUtc;
        var expiryWindow = now.AddDays(30);
        var certificates = db.AppCertificates.Where(x => !x.IsRevoked &&
            (x.Type == CertificateType.Authority || x.Type == CertificateType.Intermediate || x.Type == CertificateType.Web));
        Add("CertificatesExpired", "/administration/server/certificates", DashboardAttentionSeverity.Error,
            await certificates.CountAsync(x => x.NotAfterUtc <= now));
        Add("CertificatesExpiring", "/administration/server/certificates", DashboardAttentionSeverity.Warning,
            await certificates.CountAsync(x => x.NotAfterUtc > now && x.NotAfterUtc <= expiryWindow));

        var enabledStorage = await db.StorageProviders.Where(x => x.IsEnabled).Select(x => x.Id).ToListAsync();
        Add("Storage", "/administration/server/storage", DashboardAttentionSeverity.Error,
            storage.Count(x => enabledStorage.Contains(x.ProviderId) && x.State is not ("Checking" or "Disabled") &&
                               (!x.TargetAccessible || !x.Mounted || !x.Writable)));

        // Keep the last failure visible while a retry is queued or running; a successful terminal run clears it.
        var lastCompletedJobs = await db.ScheduledTasks.Select(x => x.History
            .Where(h => h.Status == ScheduledTaskStatus.Succeeded || h.Status == ScheduledTaskStatus.Failed || h.Status == ScheduledTaskStatus.Interrupted)
            .OrderByDescending(h => h.QueuedAtUtc).ThenByDescending(h => h.Id)
            .Select(h => (ScheduledTaskStatus?)h.Status).FirstOrDefault()).ToListAsync();
        Add("Scheduler", "/administration/server/scheduler", DashboardAttentionSeverity.Error,
            lastCompletedJobs.Count(x => x is ScheduledTaskStatus.Failed or ScheduledTaskStatus.Interrupted));
        if (release is not null && release.State != "Current")
            notices.Add(new("AgentRelease", $"Dashboard.Attention.AgentRelease.{release.State}",
                $"Dashboard.Attention.AgentRelease.{release.State}.Hint", "/administration/agent/installer",
                DashboardAttentionSeverity.Warning, Args: [release.BuiltVersion ?? "—", release.PreparedVersion ?? "—"]));
        result.Attention = notices.OrderByDescending(x => x.Severity).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
}
