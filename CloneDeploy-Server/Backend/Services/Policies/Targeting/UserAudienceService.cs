using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ResolvedAudienceUser(string Name, string Username, string Directory);
public sealed record AudienceDirectorySync(string Name, DateTime? LastSuccessfulSyncAtUtc, bool Enabled);
public sealed record ResolvedAudienceUsers(List<ResolvedAudienceUser> Users, List<AudienceDirectorySync> Directories);

public sealed class UserAudienceService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    private async Task<ClaimsPrincipal?> UserAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }

    public async Task<ServiceResult<List<UserAudience>>> ListAsync()
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null) return ServiceResult<List<UserAudience>>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<UserAudience>>.Ok(await db.Set<UserAudience>().AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult<UserAudience>> GetAsync(int id)
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null) return ServiceResult<UserAudience>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var item = await db.Set<UserAudience>().AsNoTracking().Include(x => x.Members).SingleOrDefaultAsync(x => x.Id == id);
        return item is null ? ServiceResult<UserAudience>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.AudienceNotFound)) : ServiceResult<UserAudience>.Ok(item);
    }

    public async Task<ServiceResult<ResolvedAudienceUsers>> ResolvedUsersAsync(int id, CancellationToken ct = default)
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null)
            return ServiceResult<ResolvedAudienceUsers>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await db.Set<UserAudience>().AnyAsync(x => x.Id == id, ct))
            return ServiceResult<ResolvedAudienceUsers>.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.AudienceNotFound));
        // Use the existing bulk audience resolver, including exclusions, nested groups,
        // OU descendants and identity ambiguity checks. Do not persist another member cache.
        var sids = await NotificationPolicyAudience.RecipientsAsync(db, id, ct);
        var users = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => x.Kind == DirectoryObjectKind.User && !x.Disabled && !x.IsMissing &&
                x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly &&
                x.ObjectSid != null && sids.Contains(x.ObjectSid)).ToListAsync(ct);
        var resolved = DirectoryIdentity.PreferredObjects(users).Values
            .DistinctBy(x => (x.DirectorySourceId, x.ObjectGuid))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.UserPrincipalName ?? x.AccountName)
            .Select(x => new ResolvedAudienceUser(x.Name, x.UserPrincipalName ?? x.AccountName ?? "", x.DirectorySource.Name)).ToList();
        var selectedSources = db.Set<UserAudienceMember>().Where(x => x.UserAudienceId == id).Select(x => x.DirectorySourceId);
        var directories = await db.DirectorySources.AsNoTracking()
            .Where(x => !x.ApplicationAccessOnly || selectedSources.Contains(x.Id)).OrderBy(x => x.Name)
            .Select(x => new AudienceDirectorySync(x.Name,
                x.SyncState == null || x.SyncState.LastSuccessfulSyncAtUtc == default ? null : x.SyncState.LastSuccessfulSyncAtUtc,
                x.IsEnabled && !x.ApplicationAccessOnly)).ToListAsync(ct);
        return ServiceResult<ResolvedAudienceUsers>.Ok(new(resolved, directories));
    }

    public async Task<ServiceResult<List<PolicyAudienceOption>>> OptionsAsync()
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null) return ServiceResult<List<PolicyAudienceOption>>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<PolicyAudienceOption>>.Ok(await ReadOptionsAsync(db));
    }

    public async Task<ServiceResult<List<PolicyTargetDirectoryNodeDto>>> DirectoryNodesAsync()
    {
        if (await UserAsync(Constants.Permissions.UserAudienceView) is null)
            return ServiceResult<List<PolicyTargetDirectoryNodeDto>>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var nodes = await (from node in db.DirectoryObjects.AsNoTracking()
            join source in db.DirectorySources on node.DirectorySourceId equals source.Id
            where source.Provider == DirectoryProvider.ActiveDirectory && source.IsEnabled && !source.ApplicationAccessOnly && !node.IsMissing && !node.Disabled &&
                  (node.Kind == DirectoryObjectKind.Domain || node.Kind == DirectoryObjectKind.Container || node.Kind == DirectoryObjectKind.OrganizationalUnit)
            orderby source.Name, node.Name
            select new PolicyTargetDirectoryNodeDto(source.Id, source.Name, node.ObjectGuid, node.ParentGuid,
                node.Name, node.DistinguishedName, node.Kind == DirectoryObjectKind.OrganizationalUnit, true)).ToListAsync();
        return ServiceResult<List<PolicyTargetDirectoryNodeDto>>.Ok(nodes);
    }

    internal static async Task<List<PolicyAudienceOption>> ReadOptionsAsync(CloneDeployDbContext db)
    {
        var objects = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource).Where(x => !x.DirectorySource.ApplicationAccessOnly &&
            (x.Kind == DirectoryObjectKind.User || x.Kind == DirectoryObjectKind.Group ||
             x.Kind == DirectoryObjectKind.OrganizationalUnit && x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory)).ToListAsync();
        return DirectoryIdentity.PreferredObjects(objects).Values.DistinctBy(x => (x.DirectorySourceId, x.ObjectGuid))
            .OrderBy(x => x.Name).Select(x => new PolicyAudienceOption(x.DirectorySourceId, x.ObjectGuid, x.Name, x.DirectorySource.Name, x.Kind,
                !x.IsMissing && !x.Disabled && x.DirectorySource.IsEnabled)).ToList();
    }

    public async Task<ServiceResult> SaveAsync(UserAudience input, bool delete = false)
    {
        var user = await UserAsync(Constants.Permissions.UserAudienceEdit);
        if (user is null) return ServiceResult.Fail("SharedResource.Forbidden");
        var name = input.Name.Trim();
        if (!delete && (name.Length is < 1 or > 200 || input.Description.Length > 2000 || input.Members.Count > 500 ||
            input.Members.Select(x => (x.DirectorySourceId, x.ObjectGuid, x.Exclude)).Distinct().Count() != input.Members.Count))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.EnterANameUpTo200CharactersADescription));
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockAsync(db);
        var stored = input.Id == 0 ? null : await db.Set<UserAudience>().Include(x => x.Members).SingleOrDefaultAsync(x => x.Id == input.Id);
        if (input.Id != 0 && (stored is null || stored.Revision != input.Revision))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.ThisAudienceChangedOrWasRemovedReloadBeforeSaving));
        if (delete)
        {
            if (stored is null) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.AudienceNotFound));
            if (await db.Set<PolicyUserAccess>().AnyAsync(x => x.LoginAudienceId == input.Id || x.SelfServiceAudienceId == input.Id))
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.ThisAudienceIsAssignedToAPolicyReplaceOr));
            db.Remove(stored);
        }
        else
        {
            if (await db.Set<UserAudience>().AnyAsync(x => x.Id != input.Id && x.Name.ToLower() == name.ToLower()))
                return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.AnAudienceWithThisNameAlreadyExists));
            foreach (var member in input.Members)
            {
                var item = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource).SingleOrDefaultAsync(x =>
                    x.DirectorySourceId == member.DirectorySourceId && x.ObjectGuid == member.ObjectGuid && !x.IsMissing && !x.Disabled &&
                    x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly && (x.Kind == DirectoryObjectKind.User || x.Kind == DirectoryObjectKind.Group ||
                    x.Kind == DirectoryObjectKind.OrganizationalUnit && x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory));
                var previous = stored?.Members.SingleOrDefault(x => x.DirectorySourceId == member.DirectorySourceId && x.ObjectGuid == member.ObjectGuid && x.Exclude == member.Exclude && x.IncludeDescendants == member.IncludeDescendants);
                if (item is null && previous is null) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.UserAudienceService.ASelectedDirectoryObjectIsUnavailableSyncTheDirectory));
                member.DisplayName = item is null ? previous!.DisplayName : $"{item.Name} \u00B7 {item.Kind} \u00B7 {item.DirectorySource.Name}";
                if (item is not null && item.Kind != DirectoryObjectKind.OrganizationalUnit) member.IncludeDescendants = false;
            }
            if (stored is null) { stored = new(); db.Add(stored); }
            stored.Name = name; stored.Description = input.Description.Trim(); stored.Revision = Guid.NewGuid();
            db.RemoveRange(stored.Members);
            await db.SaveChangesAsync();
            db.AddRange(input.Members.Select(x => new UserAudienceMember { UserAudienceId = stored.Id, DirectorySourceId = x.DirectorySourceId,
                ObjectGuid = x.ObjectGuid, Exclude = x.Exclude, IncludeDescendants = x.IncludeDescendants, DisplayName = x.DisplayName }));
        }
        await db.SaveChangesAsync();
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
            AuditType = AuditType.Update, ObjectType = nameof(UserAudience), ObjectId = stored!.Id, ObjectName = stored.Name,
            ObjectJson = JsonSerializer.Serialize(new { Operation = delete ? "Delete" : input.Id == 0 ? "Create" : "Update", stored.Name, stored.Description, Members = input.Members }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        input.Id = stored.Id; input.Revision = stored.Revision;
        return ServiceResult.Ok();
    }

    internal static Task LockAsync(CloneDeployDbContext db) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947236)");
}
