using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Provides Active Directory browsing, synchronization, and policy assignments.
/// </summary>
public sealed class ActiveDirectoryService(
    ICloneDeployDbFactory dbFactory,
    DirectorySyncRunner syncRunner,
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorization)
{
    private async Task<bool> IsAdminAsync()
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    public async Task<ServiceResult<DirectorySyncSummaryDto>> SyncAsync(int sourceId, CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<DirectorySyncSummaryDto>.Fail(ErrorKeys.Directory.Forbidden);

        var outcome = await syncRunner.RunAsync(sourceId, cancellationToken);
        return outcome.Summary is { } summary
            ? ServiceResult<DirectorySyncSummaryDto>.Ok(summary)
            : ServiceResult<DirectorySyncSummaryDto>.Fail(ErrorKeys.Directory.Validation, outcome.Error!);
    }

    public static IReadOnlyList<DirectoryObject> MergeSnapshot(List<DirectoryObject> existing,
        DirectorySnapshotDto snapshot, int directorySourceId, DateTime completedAt)
    {
        if (snapshot.Objects.Count == 0 || !snapshot.Objects.Any(x => x.ObjectGuid == snapshot.RootObjectGuid) ||
            snapshot.Objects.Any(x => x.DirectorySourceId != directorySourceId || x.ObjectGuid == Guid.Empty) ||
            snapshot.Objects.Select(x => x.ObjectGuid).Distinct().Count() != snapshot.Objects.Count)
            throw new InvalidOperationException("The directory snapshot is invalid; no changes were applied.");
        var byGuid = existing.ToDictionary(x => x.ObjectGuid);
        var additions = new List<DirectoryObject>();
        foreach (var item in existing) item.IsMissing = true;
        foreach (var source in snapshot.Objects)
        {
            if (!byGuid.TryGetValue(source.ObjectGuid, out var target))
            {
                target = new DirectoryObject
                {
                    DirectorySourceId = directorySourceId,
                    ObjectGuid = source.ObjectGuid
                };
                additions.Add(target);
            }

            target.ParentGuid = source.ParentGuid;
            target.Kind = source.Kind;
            target.Name = source.Name;
            target.DistinguishedName = source.DistinguishedName;
            target.AccountName = source.AccountName;
            target.UserPrincipalName = source.UserPrincipalName;
            target.Email = source.Email;
            target.DnsHostName = source.DnsHostName;
            target.ObjectSid = source.ObjectSid;
            target.Disabled = source.Disabled;
            target.IsMissing = false;
            target.IsScopeAncestor = source.IsScopeAncestor;
            target.LastSeenAtUtc = completedAt;
        }

        return additions;
    }

    public async Task<ServiceResult<DirectoryUserHistoryPage>> GetUserSelfServiceHistoryAsync(
        int directorySourceId, Guid objectGuid, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<DirectoryUserHistoryPage>.Fail(ErrorKeys.Directory.Forbidden);
        if (page < 0 || pageSize is < 1 or > 100 || (long)page * pageSize > int.MaxValue)
            return ServiceResult<DirectoryUserHistoryPage>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.ActiveDirectoryService.InvalidHistoryPage));
        await using var db = await dbFactory.CreateContextAsync();
        var user = await db.DirectoryObjects.AsNoTracking().SingleOrDefaultAsync(x => x.DirectorySourceId == directorySourceId
            && x.ObjectGuid == objectGuid && x.Kind == DirectoryObjectKind.User, ct);
        if (user is null)
            return ServiceResult<DirectoryUserHistoryPage>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectoryUserPage.ThisDirectoryUserIsNoLongerAvailable));
        if (string.IsNullOrWhiteSpace(user.ObjectSid))
            return ServiceResult<DirectoryUserHistoryPage>.Ok(new(0, [], false));
        var runs = db.Set<AgentPolicyRun>().AsNoTracking().Where(x => x.SelfService && x.UserSid == user.ObjectSid);
        var total = await runs.CountAsync(ct);
        var rows = await (from run in runs
            join policy in db.Policies on run.PolicyId equals policy.Id
            join device in db.Devices on run.DeviceId equals device.Id
            orderby run.StartedAtUtc descending, run.Id descending
            select new DirectoryUserHistoryRow(run.Id, policy.Id, policy.Name, device.Id, device.Name, run.UserName,
                run.StartedAtUtc, run.FinishedAtUtc, run.Result, run.Detail, run.Hash))
            .Skip(page * pageSize).Take(pageSize).ToListAsync(ct);
        return ServiceResult<DirectoryUserHistoryPage>.Ok(new(total, rows, true));
    }

    public async Task<ServiceResult<DirectoryObject?>> GetUserAsync(int directorySourceId, Guid objectGuid, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<DirectoryObject?>.Fail(ErrorKeys.Directory.Forbidden);
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<DirectoryObject?>.Ok(await db.DirectoryObjects.AsNoTracking()
            .Include(x => x.DirectorySource)
            .SingleOrDefaultAsync(x => x.DirectorySourceId == directorySourceId && x.ObjectGuid == objectGuid
                && x.Kind == DirectoryObjectKind.User, ct));
    }

    public async Task<ServiceResult<List<DirectoryObject>>> GetUsersAsync(int directorySourceId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectoryObject>>.Fail(ErrorKeys.Directory.Forbidden);
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectoryObject>>.Ok(await db.DirectoryObjects.AsNoTracking()
            .Where(x => x.DirectorySourceId == directorySourceId && x.Kind == DirectoryObjectKind.User && !x.IsMissing)
            .OrderBy(x => x.Name).ToListAsync(ct));
    }

    public async Task<ServiceResult<List<DirectoryObject>>> GetObjectsAsync(int directorySourceId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectoryObject>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectoryObject>>.Ok(await db.DirectoryObjects.AsNoTracking()
            .Where(x => x.DirectorySourceId == directorySourceId && !x.IsMissing)
            .OrderBy(x => x.Name).ToListAsync(ct));
    }

    public async Task<ServiceResult<DirectorySyncState?>> GetSyncStateAsync(int directoryId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<DirectorySyncState?>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<DirectorySyncState?>.Ok(await db.DirectorySyncStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DirectorySourceId == directoryId, ct));
    }

    public async Task<ServiceResult<List<DirectoryPolicyChoiceDto>>> GetPolicyChoicesAsync(CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectoryPolicyChoiceDto>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectoryPolicyChoiceDto>>.Ok(await db.Policies.AsNoTracking().Where(x => !x.Archived).OrderBy(x => x.Name)
            .Select(x => new DirectoryPolicyChoiceDto(x.Id, x.Name)).ToListAsync(ct));
    }

    public async Task<ServiceResult<List<DirectoryPolicyLinkDto>>> GetOuPoliciesAsync(int directorySourceId, Guid ouGuid, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectoryPolicyLinkDto>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectoryPolicyLinkDto>>.Ok(await (from link in db.DirectoryOuPolicies.AsNoTracking()
            join policy in db.Policies on link.PolicyId equals policy.Id
            where link.DirectorySourceId == directorySourceId && link.OuGuid == ouGuid
            orderby link.Order, policy.Name
            select new DirectoryPolicyLinkDto(policy.Id, policy.Name, link.Order, link.IncludeDescendants)).ToListAsync(ct));
    }

    public async Task<ServiceResult> AssignPolicyAsync(int directorySourceId, Guid ouGuid, int policyId, bool includeDescendants, int order,
        CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(ErrorKeys.Directory.Forbidden);

        if (order < 0)
            return ServiceResult.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.ActiveDirectoryService.PolicyOrderMustBeZeroOrGreater));

        await using var db = await dbFactory.CreateContextAsync();
        var key = directorySourceId;
        if (!await db.DirectoryObjects.AnyAsync(x => x.DirectorySourceId == key && x.ObjectGuid == ouGuid &&
                                                     x.Kind == DirectoryObjectKind.OrganizationalUnit && !x.IsMissing && x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly, ct))
            return ServiceResult.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.ActiveDirectoryService.SelectACurrentlySyncedOrganizationalUnit));

        if (!await db.Policies.AnyAsync(x => x.Id == policyId && !x.Archived, ct))
            return ServiceResult.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.ActiveDirectoryService.SelectAnActivePolicy));

        var link = await db.DirectoryOuPolicies.SingleOrDefaultAsync(x => x.DirectorySourceId == key &&
                                                                          x.OuGuid == ouGuid && x.PolicyId == policyId, ct);
        if (link is null)
        {
            link = new DirectoryOuPolicy
            {
                DirectorySourceId = key,
                OuGuid = ouGuid,
                PolicyId = policyId
            };
            db.DirectoryOuPolicies.Add(link);
        }

        link.IncludeDescendants = includeDescendants;
        link.Order = order;
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RemovePolicyAsync(int directorySourceId, Guid ouGuid, int policyId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        await db.DirectoryOuPolicies.Where(x => x.DirectorySourceId == directorySourceId &&
                                                x.OuGuid == ouGuid && x.PolicyId == policyId).ExecuteDeleteAsync(ct);
        return ServiceResult.Ok();
    }
}
