using System.Buffers.Binary;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class NotificationPolicyAudience
{
    internal const string ValidationMessage = "Recipient audiences require a notification-only policy: remove other actions, conditions, inventory, login tracking and completion tasks, or clear the recipient audience in Targeting.";
    internal static bool Eligible(Policy p, IEnumerable<PolicyAction> links) =>
        !p.RunInventory && !p.RunLoginTracker && p.RemoteAccess == PolicyRemoteAccess.NotConfigured &&
        p.CompletedAction == PolicyCompletedAction.DoNothing && p.ConditionId == null &&
        p.ExecutionType == PolicyExecutionType.Install && links.Any() &&
        links.All(x => x.ActionType == ActionType.Notification && x.ConditionId == null);

    internal static async Task<bool> EligibleAsync(CloneDeployDbContext db, int id)
    {
        var policy = await db.Policies.AsNoTracking().SingleAsync(x => x.Id == id);
        return Eligible(policy, await db.PolicyActions.AsNoTracking().Where(x => x.PolicyId == id).ToListAsync());
    }

    // Resolve a delivery-time snapshot in a bounded number of queries. Include both the
    // on-premises SID and the Entra Windows SID for the same recognized directory identity.
    internal static async Task<string[]> RecipientsAsync(CloneDeployDbContext db, int audienceId, CancellationToken ct)
    {
        var members = await db.Set<UserAudienceMember>().AsNoTracking().Where(x => x.UserAudienceId == audienceId).ToArrayAsync(ct);
        var objects = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly).ToArrayAsync(ct);
        var lookup = objects.ToDictionary(x => (x.DirectorySourceId, x.ObjectGuid));
        bool Available(DirectoryObject x) => !x.IsMissing && !x.Disabled &&
            (x.Kind is DirectoryObjectKind.User or DirectoryObjectKind.Group || x.Kind == DirectoryObjectKind.OrganizationalUnit && x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory);
        if (members.Any(m => m.Exclude && (!lookup.TryGetValue((m.DirectorySourceId, m.ObjectGuid), out var x) || !Available(x)))) return [];
        var memberships = (await db.DirectoryEffectiveGroupMemberships.AsNoTracking().ToArrayAsync(ct))
            .ToLookup(x => (x.DirectorySourceId, x.MemberGuid));
        var equivalents = objects.Where(x => x.Kind == DirectoryObjectKind.Group && Available(x) && !string.IsNullOrWhiteSpace(x.ObjectSid))
            .GroupBy(x => x.ObjectSid!).Where(g => g.GroupBy(x => x.DirectorySource.Provider).All(p => p.Count() == 1))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var userObjects = objects.Where(x => x.Kind == DirectoryObjectKind.User).ToArray();
        var bySid = userObjects.Where(x => !string.IsNullOrWhiteSpace(x.ObjectSid)).ToLookup(x => x.ObjectSid!);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in userObjects.GroupBy(x => string.IsNullOrWhiteSpace(x.ObjectSid) ? $"object:{x.DirectorySourceId}:{x.ObjectGuid}" : x.ObjectSid!))
        {
            ct.ThrowIfCancellationRequested();
            var users = identity.ToArray();
            if (users.GroupBy(x => x.DirectorySource.Provider).Any(g => g.Count() > 1) || users.Any(x => x.Disabled && !x.IsMissing)) continue;
            users = users.Where(x => !x.IsMissing).ToArray();
            var direct = new HashSet<(int, Guid)>(); var ancestors = new HashSet<(int, Guid)>();
            foreach (var user in users)
            {
                direct.Add((user.DirectorySourceId, user.ObjectGuid));
                foreach (var membership in memberships[(user.DirectorySourceId, user.ObjectGuid)])
                    if (lookup.TryGetValue((user.DirectorySourceId, membership.GroupGuid), out var group) && group.Kind == DirectoryObjectKind.Group && Available(group))
                    {
                        direct.Add((group.DirectorySourceId, group.ObjectGuid));
                        if (group.ObjectSid is { } sid && equivalents.TryGetValue(sid, out var peers))
                            foreach (var peer in peers) direct.Add((peer.DirectorySourceId, peer.ObjectGuid));
                    }
                if (user.DirectorySource.Provider != DirectoryProvider.ActiveDirectory) continue;
                var parent = user.ParentGuid;
                if (parent is { } first && lookup.TryGetValue((user.DirectorySourceId, first), out var item) && !item.IsMissing)
                    direct.Add((user.DirectorySourceId, first));
                var seen = new HashSet<Guid>();
                while (parent is { } id && seen.Add(id) && lookup.TryGetValue((user.DirectorySourceId, id), out var ancestor) && !ancestor.IsMissing)
                { ancestors.Add((user.DirectorySourceId, id)); parent = ancestor.ParentGuid; }
            }
            bool Matches(UserAudienceMember m) => lookup.TryGetValue((m.DirectorySourceId, m.ObjectGuid), out var x) && Available(x) &&
                (direct.Contains((m.DirectorySourceId, m.ObjectGuid)) || m.IncludeDescendants && ancestors.Contains((m.DirectorySourceId, m.ObjectGuid)));
            if (!members.Any(m => !m.Exclude && Matches(m)) || members.Any(m => m.Exclude && Matches(m))) continue;
            foreach (var user in users)
            {
                if (user.ObjectSid is { Length: > 0 and <= 184 } sid && sid.StartsWith("S-1-", StringComparison.Ordinal)) result.Add(sid);
                if (user.DirectorySource.Provider == DirectoryProvider.EntraId)
                {
                    var bytes = user.ObjectGuid.ToByteArray();
                    var cloudSid = "S-1-12-1-" + string.Join("-", Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4))));
                    // An alias collision must fail closed just like DirectoryIdentity.UsersAsync.
                    var aliases = bySid[cloudSid].Append(user).DistinctBy(x => (x.DirectorySourceId, x.ObjectGuid)).ToArray();
                    if (aliases.Select(x => x.ObjectSid).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count() <= 1 &&
                        aliases.GroupBy(x => x.DirectorySource.Provider).All(g => g.Count() == 1) && !aliases.Any(x => x.Disabled && !x.IsMissing)) result.Add(cloudSid);
                }
            }
        }
        // Validate every emitted SID using the same ambiguity/disabled identity rules as
        // interactive directory sign-in, including collisions between cloud and stored SIDs.
        var cloudAliases = userObjects.Where(x => x.DirectorySource.Provider == DirectoryProvider.EntraId).ToLookup(x =>
        {
            var bytes = x.ObjectGuid.ToByteArray();
            return "S-1-12-1-" + string.Join("-", Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4))));
        });
        result.RemoveWhere(sid =>
        {
            var users = bySid[sid].Concat(cloudAliases[sid]).DistinctBy(x => (x.DirectorySourceId, x.ObjectGuid)).ToArray();
            var sids = users.Select(x => x.ObjectSid).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            if (sids.Length > 1) return true;
            if (sids.Length == 1 && sids[0] != sid) users = bySid[sids[0]!].ToArray();
            return users.GroupBy(x => x.DirectorySource.Provider).Any(g => g.Count() > 1) ||
                users.Any(x => x.Disabled && !x.IsMissing) || users.All(x => x.IsMissing);
        });
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}
