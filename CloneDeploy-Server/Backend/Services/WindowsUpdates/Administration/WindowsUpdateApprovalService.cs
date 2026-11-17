using System.Data;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WindowsUpdateReportRow(int Id, string? UpdateId, int? RevisionNumber, string? Title,
    string[]? KBArticleIds, string? Category, string? Severity, bool Approved, string? ApprovedBy, DateTime? ApprovedAtUtc, int Installed, int Missing)
{
    public bool AutoApprovalBlocked { get; init; }
    public DateTime? RevisionFirstSeenAtUtc { get; init; }
    public bool CanSelect => Guid.TryParse(UpdateId, out var id) && id != Guid.Empty && RevisionNumber is >= 0;
    public WindowsUpdateIdentity Identity => new(Guid.Parse(UpdateId!), RevisionNumber!.Value);
}

/// <summary>Lists inventoried updates and audits approval changes for exact update revisions.</summary>
public sealed class WindowsUpdateApprovalService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, TimeProvider clock, ILogger<WindowsUpdateApprovalService> logger)
{
    public Task<ServiceResult<List<WindowsUpdateReportRow>>> GetAsync(string filter, string? search) => ServiceOperations.RunAsync(async () =>
    {
        if (await AdminAsync() is null) return ServiceResult<List<WindowsUpdateReportRow>>.Fail(ErrorKeys.Action.Forbidden);
        await using var db = await factory.CreateContextAsync();
        var query = db.WindowsUpdates.AsNoTracking();
        if (filter == "Approved") query = query.Where(x => x.ApprovedRevisionNumber != null && x.ApprovedRevisionNumber == x.RevisionNumber);
        else if (filter == "Unapproved") query = query.Where(x => x.ApprovedRevisionNumber == null || x.RevisionNumber == null || x.ApprovedRevisionNumber != x.RevisionNumber);
        else if (filter != "All") return ServiceResult<List<WindowsUpdateReportRow>>.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateApprovalService.InvalidApprovalFilter));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim().ToLower();
            query = query.Where(x => (x.Title != null && x.Title.ToLower().Contains(text)) || (x.UpdateId != null && x.UpdateId.ToLower().Contains(text)) ||
                (x.KBArticleIds != null && x.KBArticleIds.Any(k => k.ToLower().Contains(text))));
        }
        return ServiceResult<List<WindowsUpdateReportRow>>.Ok(await query.OrderBy(x => x.Title).ThenBy(x => x.Id).Select(x => new WindowsUpdateReportRow(
            x.Id, x.UpdateId, x.RevisionNumber, x.Title, x.KBArticleIds, x.Category, x.MsrcSeverity,
            x.ApprovedRevisionNumber != null && x.ApprovedRevisionNumber == x.RevisionNumber,
            x.ApprovedBy, x.ApprovedAtUtc,
            db.DeviceWindowsUpdates.Count(d => d.WindowsUpdateId == x.Id && d.IsInstalled && d.Device!.ArchivedAtUtc == null),
            db.DeviceWindowsUpdates.Count(d => d.WindowsUpdateId == x.Id && !d.IsInstalled && d.Device!.ArchivedAtUtc == null))
            { AutoApprovalBlocked = x.AutoApprovalBlockedRevision != null && x.AutoApprovalBlockedRevision == x.RevisionNumber,
                RevisionFirstSeenAtUtc = x.RevisionFirstSeenAtUtc }).ToListAsync());
    }, logger);

    public Task<ServiceResult> SetApprovalAsync(IReadOnlyList<WindowsUpdateIdentity> selections, bool approved) => ChangeApprovalAsync(selections, approved);

    public Task<ServiceResult> AllowAutoApprovalAsync(IReadOnlyList<WindowsUpdateIdentity> selections) => ChangeApprovalAsync(selections, null);

    private Task<ServiceResult> ChangeApprovalAsync(IReadOnlyList<WindowsUpdateIdentity> selections, bool? approved) => ServiceOperations.RunAsync(async () =>
    {
        var user = await AdminAsync();
        if (user is null) return ServiceResult.Fail(ErrorKeys.Action.Forbidden);
        if (selections.Count is < 1 or > 1000 || selections.Any(x => x.UpdateId == Guid.Empty || x.RevisionNumber < 0) || selections.Select(x => x.UpdateId).Distinct().Count() != selections.Count)
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateApprovalService.SelectBetween1And1000DistinctUpdatesWith));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await WindowsUpdateAutoApproval.LockAsync(db);
        var ids = selections.Select(x => x.UpdateId.ToString()).ToArray();
        var updates = await db.WindowsUpdates.Where(x => x.UpdateId != null && ids.Contains(x.UpdateId.ToLower())).ToListAsync();
        if (updates.Count != selections.Count || updates.Any(x => !selections.Any(s => s.UpdateId == Guid.Parse(x.UpdateId!) && s.RevisionNumber == x.RevisionNumber)))
            return ServiceResult.Fail(ErrorKeys.Action.Validation, UiText.Get(LocKeys.Ui.WindowsUpdateApprovalService.AnUpdateRevisionChangedOrDisappearedRefreshTheList));
        foreach (var update in updates)
        {
            update.AutoApprovalBlockedRevision = approved == false ? update.RevisionNumber : null;
            if (approved is { } manualApproval)
            {
                update.ApprovedRevisionNumber = manualApproval ? update.RevisionNumber : null;
                update.ApprovedAtUtc = manualApproval ? clock.GetUtcNow().UtcDateTime : null;
                update.ApprovedBy = manualApproval ? user.Identity!.Name : null;
                update.ApprovedByRuleId = null;
            }
            db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
                AuditType = AuditType.Update, ObjectType = nameof(WindowsUpdate), ObjectId = update.Id, ObjectName = update.Title ?? update.UpdateId!,
                ObjectJson = JsonSerializer.Serialize(new { update.UpdateId, update.RevisionNumber, Approved = approved,
                    AutoApprovalBlocked = update.AutoApprovalBlockedRevision == update.RevisionNumber }) });
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return ServiceResult.Ok();
    }, logger);

    private async Task<ClaimsPrincipal?> AdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) &&
            (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportWindowsUpdates)).Succeeded ? user : null;
    }
}
