using System.Text;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

public sealed record EmailDigestContent(string Subject, string Body, int AttentionCount);
public sealed partial class EmailDigestBuilder(ICloneDeployDbFactory factory, AppUserPermissionService permissions, ServerUrlSettingsStore urls)
{
    public required ServerUpdateChecker Updates { private get; init; }
    public CertificateExpiryNotices? Certificates { private get; init; }
    public ProfessionalSupport? Support { private get; init; }

    internal static int AppendSupportNotice(StringBuilder body, ProfessionalSupportStatus? status, bool admin, DateTime now, string link)
    {
        if (!admin || status?.ExpirationNotice(DateOnly.FromDateTime(now)) is not { } notice) return 0;
        body.AppendLine().AppendLine(notice)
            .AppendLine($"Last verified: {status.VerifiedAt:MM-dd-yyyy HH:mm} UTC (cached coverage).")
            .AppendLine("Review coverage and contact support: " + link);
        if (status.Error is not null) body.AppendLine(status.Error);
        return 1;
    }

    public async Task<EmailDigestContent?> BuildAsync(UserEmailSubscription subscription, bool weekly, DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subscription.AppUserId, ct);
        if (user is null || user.LockoutEnd > now) return null;
        var access = await permissions.GetDirectoryAccessAsync(user.Id);
        if (access.Managed && !access.Allowed) return null;
        var rights = await permissions.GetEffectivePermissionsAsync(user.Id);
        if (!rights.HasData) return null;
        var admin = access.Allowed && access.Administrator;
        bool Can(string permission) => admin || rights.Data.Selected.Contains(permission);
        var url = await urls.RequireAsync(ct);
        string Link(string path) => url.TrimEnd('/') + "/" + path.TrimStart('/');
        var since = now.AddDays(weekly ? -7 : -1);
        var body = new StringBuilder($"CloneDeploy {(weekly ? "weekly summary" : "daily attention report")}\nGenerated {now:yyyy-MM-dd HH:mm} UTC\nFailures: {since:yyyy-MM-dd HH:mm} to {now:yyyy-MM-dd HH:mm} UTC. Pending approvals and device warnings reflect current data.\n");
        var attention = 0;
        if (subscription.Security && admin)
            attention += await AppendSecurityAsync(db, body, since, now, ct);
        if (subscription.Security && admin && Certificates is not null)
        {
            var notices = await Certificates.ReadAsync(db, now, ct: ct);
            foreach (var notice in notices)
            {
                attention++;
                body.AppendLine().AppendLine(notice.Name + ": " + (notice.ExpiresUtc is null
                    ? "Unable to read certificate expiration. Check the configured credential."
                    : $"{(notice.Expired ? "Expired" : "Expires")} {notice.ExpiresUtc:yyyy-MM-dd HH:mm} UTC."));
                body.AppendLine(notice.Key == "EntraCertificate"
                    ? "Upload and activate a replacement credential in Entra and CloneDeploy."
                    : notice.Key == "ExternalHttpsCertificate"
                        ? "Renew through your certificate provider and apply it to the server HTTPS configuration."
                        : "Review and renew the certificate. Main server HTTPS renewal requires a server restart.");
                body.AppendLine(Link(notice.Href));
            }
        }
        if (subscription.HelpDesk)
            attention += await AppendHelpDeskAsync(db, body, new HelpDeskService.Access(user.Id,
                Can(Constants.Permissions.HelpDeskViewAll), Can(Constants.Permissions.HelpDeskViewAssigned),
                Can(Constants.Permissions.HelpDeskViewUnassigned), false), now, url, ct);
        async Task Section(string title, string path, IQueryable<string> names)
        {
            var count = await names.CountAsync(ct);
            if (count == 0) return;
            attention += count;
            body.AppendLine().AppendLine($"{title}: {count:N0}");
            foreach (var name in await names.OrderBy(x => x).Take(10).ToListAsync(ct)) body.AppendLine("  - " + name.Replace('\r', ' ').Replace('\n', ' '));
            if (count > 10) body.AppendLine($"  ... and {count - 10:N0} more");
            body.AppendLine(Link(path));
        }
        if (subscription.Approvals && Can(Constants.Permissions.DeviceApprove))
        {
            await Section("Devices awaiting enrollment approval", "/devices/approval-and-reset", db.AgentProvisioningRequests.Where(x => x.State == AgentProvisioningState.PendingApproval).Select(x => x.DeviceName));
            await Section("Devices awaiting reset approval", "/devices/approval-and-reset", db.AgentProvisioningRequests.Where(x => x.State == AgentProvisioningState.PendingReset).Select(x => x.DeviceName));
        }
        if (subscription.Imaging && admin)
            await Section("Failed imaging tasks", "/tasks/imaging", db.ActiveImagingTasks.Where(x => x.Status == ImagingStatus.Failed && x.LastUpdatedAtUtc >= since && x.LastUpdatedAtUtc <= now).Select(x => x.Device == null ? "Deleted device" : x.Device.Name));
        if (subscription.Actions && admin)
            await Section("Failed action runs", "/tasks/deployments", db.DeviceTasks.Where(x => x.Status == DeviceTaskStatus.Failed && x.FinishedAtUtc >= since && x.FinishedAtUtc <= now).Select(x => x.DeviceName + " / " + x.Deployment.ActionName));
        if (subscription.Policies && Can(Constants.Permissions.PolicyView) && Can(Constants.Permissions.DeviceView))
            await Section("Failed policy runs", "/reports/user", from run in db.Set<AgentPolicyRun>() join device in db.Devices on run.DeviceId equals device.Id
                join policy in db.Policies on run.PolicyId equals policy.Id where run.Result == 1 && run.FinishedAtUtc >= since && run.FinishedAtUtc <= now select device.Name + " / " + policy.Name);
        if (subscription.DirectorySync && admin)
            await Section("Failed directory sync jobs", "/administration/server/scheduler", db.Set<ScheduledTaskExecution>().Where(x => x.TaskId == ScheduledTaskType.DirectorySync
                && (x.Status == ScheduledTaskStatus.Failed || x.Status == ScheduledTaskStatus.Interrupted) && x.FinishedAtUtc >= since && x.FinishedAtUtc <= now).Select(x => x.TargetName ?? "Directory sync"));
        if (weekly && subscription.DeviceWarnings && Can(Constants.Permissions.DeviceView))
        {
            var devices = db.Devices.Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
            body.AppendLine().AppendLine($"Current fleet: {await devices.CountAsync(ct):N0} non-archived devices");
            await Section("Low disk space (10 GiB or 10% free or less)", "/reports/devices", devices.Where(x => x.LogicalVolumes.Any(v => v.SizeGB > 0 && v.FreeSpaceGB >= 0 && v.FreeSpaceGB <= v.SizeGB && v.FreeSpacePercent >= 0 && v.FreeSpacePercent <= 100 && (v.FreeSpaceGB <= 10 || v.FreeSpacePercent <= 10))).Select(x => x.Name));
            var warnings = new[] { "pred fail", "degraded", "error", "nonrecover", "stressed", "failed", "failing" };
            await Section("Reported disk warnings", "/reports/devices", devices.Where(x => x.HardDrives.Any(d => d.Status != null && warnings.Contains(d.Status.Trim().ToLower()))).Select(x => x.Name));
            await Section("Stale or missing check-ins (over 30 days)", "/reports/devices", devices.Where(x => x.LastCheckinAtUtc == null || x.LastCheckinAtUtc < now.AddDays(-30)).Select(x => x.Name));
            await Section("Stale or missing inventory (over 30 days)", "/reports/devices", devices.Where(x => x.LastInventoryAtUtc == null || x.LastInventoryAtUtc < now.AddDays(-30)).Select(x => x.Name));
            body.AppendLine("Disk warnings use reported Windows status, not detailed SMART telemetry. Inventory can be stale; missing data is not proof of health.");
            body.AppendLine(Link("/reports/devices"));
        }
        if (subscription.ServerUpdates && admin)
        {
            var update = await Updates.CheckAsync().WaitAsync(ct);
            if (update.Error is null && update.UpdateAvailable)
            {
                attention++;
                body.AppendLine().AppendLine($"CloneDeploy server update available: {update.LatestVersion}")
                    .AppendLine($"Installed version: {update.InstalledVersion}")
                    .AppendLine("View release and downloads: " + ServerUpdateChecker.ReleasePage);
            }
        }
        attention += AppendSupportNotice(body, Support?.Status, admin, now, Link("/administration/server/support"));
        if (attention == 0) body.AppendLine("No items need attention in your selected, permitted categories.");
        body.AppendLine().AppendLine("Manage subscriptions: " + Link($"/users/manage/{subscription.AppUserId}/options"));
        return new($"CloneDeploy {(weekly ? "weekly summary" : "daily attention")} — {now:yyyy-MM-dd}", body.ToString(), attention);
    }
}
