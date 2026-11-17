using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectoryApplicationAccessResult(bool Managed, bool Allowed, bool Administrator, int[] GroupIds);

public static class DirectoryApplicationAccess
{
    private static readonly DirectoryApplicationAccessResult Denied = new(true, false, false, []);

    public static async Task<DirectoryApplicationAccessResult> ForUserAsync(CloneDeployDbContext db, int userId, CancellationToken ct = default)
    {
        var logins = await db.UserLogins.AsNoTracking().Where(x => x.UserId == userId && (x.LoginProvider == "Entra" || x.LoginProvider == "ActiveDirectory")).ToListAsync(ct);
        var directGroups = await db.AppUserGroupMemberships.Where(x => x.AppUserId == userId).Select(x => x.AppUserGroupId).ToListAsync(ct);
        if (logins.Count == 0)
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
            return new(false, user is not null, user is not null && (user.Membership == Constants.UserMembership.Admin ||
                await db.AppUserGroups.AnyAsync(x => directGroups.Contains(x.Id) && x.GrantAdministrator, ct)), directGroups.ToArray());
        }
        var results = new List<DirectoryApplicationAccessResult>();
        foreach (var login in logins)
            results.Add(login.LoginProvider == "Entra" ? await ForEntraAsync(db, login.ProviderKey, ct) : await ForAdAsync(db, login.ProviderKey, ct));
        // Direct membership cannot restore access revoked by directory policy.
        if (!results.Any(x => x.Allowed)) return Denied;
        var ids = directGroups.Concat(results.Where(x => x.Allowed).SelectMany(x => x.GroupIds)).Distinct().ToArray();
        return new(true, true, await db.AppUserGroups.AnyAsync(x => ids.Contains(x.Id) && x.GrantAdministrator, ct), ids);
    }

    public static async Task<DirectoryApplicationAccessResult> ForAdAsync(CloneDeployDbContext db, string key, CancellationToken ct = default)
    {
        var parts = key.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var sourceId) || !Guid.TryParse(parts[1], out var guid)) return Denied;
        var user = await db.DirectoryObjects.AsNoTracking().SingleOrDefaultAsync(x => x.DirectorySourceId == sourceId &&
            x.ObjectGuid == guid && x.Kind == DirectoryObjectKind.User && !x.IsMissing &&
            x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory && x.DirectorySource.IsEnabled && x.DirectorySource.SyncState != null, ct);
        if (user is null || user.Disabled) return Denied;
        var identities = new List<DirectoryObject> { user };
        if (user.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true)
        {
            if (await db.DirectoryObjects.CountAsync(x => x.DirectorySourceId == sourceId && x.Kind == DirectoryObjectKind.User &&
                x.ObjectSid == user.ObjectSid && !x.IsMissing, ct) != 1) return Denied;
            var cloud = await db.DirectoryObjects.AsNoTracking().Where(x => x.Kind == DirectoryObjectKind.User && !x.IsMissing &&
                x.ObjectSid == user.ObjectSid && x.DirectorySource.Provider == DirectoryProvider.EntraId && x.DirectorySource.IsEnabled &&
                db.EntraConnections.Any(e => e.TenantId == x.DirectorySource.SyncState.RootObjectGuid)).ToListAsync(ct);
            if (cloud.Count > 1 || cloud.Any(x => x.Disabled)) return Denied;
            identities.AddRange(cloud);
        }
        var groupIds = new HashSet<int>();
        foreach (var identity in identities)
        {
            var matched = await (from mapping in db.Set<AppUserGroupDirectoryMapping>()
                join groupObject in db.DirectoryObjects on new { mapping.DirectorySourceId, mapping.ObjectGuid }
                    equals new { groupObject.DirectorySourceId, groupObject.ObjectGuid }
                where mapping.DirectorySourceId == identity.DirectorySourceId && groupObject.Kind == DirectoryObjectKind.Group &&
                    !groupObject.IsMissing && !groupObject.Disabled && db.DirectoryEffectiveGroupMemberships.Any(m =>
                        m.DirectorySourceId == identity.DirectorySourceId && m.MemberGuid == identity.ObjectGuid && m.GroupGuid == mapping.ObjectGuid)
                select mapping.AppUserGroupId).ToListAsync(ct);
            groupIds.UnionWith(matched);
        }
        var ids = groupIds.ToArray();
        return new(true, ids.Length > 0, await db.AppUserGroups.AnyAsync(x => ids.Contains(x.Id) && x.GrantAdministrator, ct), ids);
    }

    public static async Task<DirectoryApplicationAccessResult> ForEntraAsync(CloneDeployDbContext db, string key, CancellationToken ct = default)
    {
        var parts = key.Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var tenant) || !Guid.TryParse(parts[1], out var oid)) return Denied;
        if (!await db.EntraConnections.AnyAsync(x => x.TenantId == tenant, ct)) return Denied;
        var sources = await db.DirectorySources.AsNoTracking().Include(x => x.SyncState).Where(x => x.IsEnabled &&
            x.SyncState != null && x.SyncState.LastSuccessfulSyncAtUtc != default).ToListAsync(ct);
        var cloudSources = sources.Where(x => x.Provider == DirectoryProvider.EntraId && x.SyncState.RootObjectGuid == tenant).Select(x => x.Id).ToArray();
        var cloud = await db.DirectoryObjects.AsNoTracking().Where(x => cloudSources.Contains(x.DirectorySourceId) &&
            x.Kind == DirectoryObjectKind.User && x.ObjectGuid == oid && !x.IsMissing).ToListAsync(ct);
        if (cloud.Count != 1 || cloud[0].Disabled) return Denied;
        var identities = new List<DirectoryObject> { cloud[0] };
        if (cloud[0].ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true)
        {
            var adSources = sources.Where(x => x.Provider == DirectoryProvider.ActiveDirectory).Select(x => x.Id).ToArray();
            var sid = cloud[0].ObjectSid;
            var ad = await db.DirectoryObjects.AsNoTracking().Where(x => adSources.Contains(x.DirectorySourceId) &&
                x.Kind == DirectoryObjectKind.User && x.ObjectSid == sid && !x.IsMissing).ToListAsync(ct);
            if (ad.Count > 1 || ad.Any(x => x.Disabled)) return Denied;
            identities.AddRange(ad);
        }
        var groupIds = new HashSet<int>();
        foreach (var identity in identities)
        {
            var matched = await (from mapping in db.Set<AppUserGroupDirectoryMapping>()
                join groupObject in db.DirectoryObjects on new { mapping.DirectorySourceId, mapping.ObjectGuid }
                    equals new { groupObject.DirectorySourceId, groupObject.ObjectGuid }
                where mapping.DirectorySourceId == identity.DirectorySourceId && groupObject.Kind == DirectoryObjectKind.Group &&
                    !groupObject.IsMissing && !groupObject.Disabled && db.DirectoryEffectiveGroupMemberships.Any(m =>
                        m.DirectorySourceId == identity.DirectorySourceId && m.MemberGuid == identity.ObjectGuid && m.GroupGuid == mapping.ObjectGuid)
                select mapping.AppUserGroupId).ToListAsync(ct);
            groupIds.UnionWith(matched);
        }
        var ids = groupIds.ToArray();
        return new(true, ids.Length > 0, await db.AppUserGroups.AnyAsync(x => ids.Contains(x.Id) && x.GrantAdministrator, ct), ids);
    }
}
