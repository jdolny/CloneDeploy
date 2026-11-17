using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

/// <summary>Shared eligibility rules for policy listing and admission, using Windows SID identity and synced audiences.</summary>
internal static class PolicyAudienceDelivery
{
    internal static async Task<(bool Login, bool SelfService)> PreviewUserAsync(CloneDeployDbContext db,
        string? sid, int? loginAudienceId, int? selfServiceAudienceId, CancellationToken ct)
    {
        var ids = new[] { loginAudienceId, selfServiceAudienceId }.OfType<int>().ToArray();
        var matches = await MatchingAudiencesAsync(db, sid, ids, ct);
        return (loginAudienceId is int login && matches.Contains(login), selfServiceAudienceId is int portal && matches.Contains(portal));
    }

    internal static async Task<HashSet<int>> MatchingAudiencesAsync(CloneDeployDbContext db, string? sid, int[] ids, CancellationToken ct)
    {
        var (direct, ancestors) = await ReadUserMembershipAsync(db, sid, ct);
        if (direct.Count == 0 || ids.Length == 0) return [];
        var members = await db.Set<UserAudienceMember>().AsNoTracking().Where(x => ids.Contains(x.UserAudienceId)).ToListAsync(ct);
        var sourceIds = members.Select(x => x.DirectorySourceId).Distinct().ToArray();
        var available = (await db.DirectoryObjects.AsNoTracking().Where(x => sourceIds.Contains(x.DirectorySourceId) &&
            x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly && !x.IsMissing && !x.Disabled &&
            (x.Kind == DirectoryObjectKind.User || x.Kind == DirectoryObjectKind.Group ||
             x.Kind == DirectoryObjectKind.OrganizationalUnit && x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory))
            .Select(x => new { x.DirectorySourceId, x.ObjectGuid }).ToListAsync(ct))
            .Select(x => (x.DirectorySourceId, x.ObjectGuid)).ToHashSet();
        bool Matches(UserAudienceMember x) => available.Contains((x.DirectorySourceId, x.ObjectGuid)) &&
            (direct.Contains((x.DirectorySourceId, x.ObjectGuid)) || x.IncludeDescendants && ancestors.Contains((x.DirectorySourceId, x.ObjectGuid)));
        // Missing exclusions cannot silently turn a restricted audience into a broader one.
        return members.GroupBy(x => x.UserAudienceId).Where(g => g.Any(x => !x.Exclude && Matches(x)) &&
            !g.Any(x => x.Exclude && (!available.Contains((x.DirectorySourceId, x.ObjectGuid)) || Matches(x))))
            .Select(g => g.Key).ToHashSet();
    }

    internal static async Task<List<CompiledPolicySnapshotDto>> ReadAsync(CloneDeployDbContext db, int deviceId, string? sid, bool selfService, CancellationToken ct)
    {
        if (selfService && !(await AgentFeatureSettings.FeaturesAsync(db, ct)).SelfServiceEnabled) return [];
        if (selfService && (sid is null || sid.Length > 184 || !System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-1-\d+(?:-\d+){1,15}\z"))) return [];
        if (!await db.Devices.AnyAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null && x.ProvisionStatus == DeviceProvisionStatus.Provisioned, ct)) return [];
        var devicePolicies = await PolicyDelivery.ReadAsync(db, deviceId, ct);
        if (await db.Set<ImagingDeployment>().AnyAsync(x => x.DeviceId == deviceId && x.ImagingCompletedAtUtc != null &&
            x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null, ct)) return selfService || sid is not null ? [] : devicePolicies;
        var settings = await db.Set<PolicyUserAccess>().AsNoTracking().ToListAsync(ct);
        var automaticIds = settings.Where(x => x.LoginAudienceId != null).Select(x => x.PolicyId).ToHashSet();
        var selected = settings.Select(x => selfService ? x.SelfServiceAudienceId : x.LoginAudienceId).OfType<int>().Distinct().ToArray();
        var matches = await MatchingAudiencesAsync(db, sid, selected, ct);
        var matchingIds = settings.Where(x => (selfService ? x.SelfServiceAudienceId : x.LoginAudienceId) is int id && matches.Contains(id)).Select(x => x.PolicyId).ToHashSet();
        var deviceIds = devicePolicies.Select(x => x.Policy.Id).ToHashSet();
        if (selfService)
        {
            var access = await db.Set<PolicyUserAccess>().Where(x => x.SelfServiceEnabled)
                .Select(x => new { x.PolicyId, x.AnyInteractiveUser, x.RestrictSelfServiceToDeviceTargets }).ToListAsync(ct);
            matchingIds = access.Where(x => x.AnyInteractiveUser
                ? deviceIds.Contains(x.PolicyId)
                : matchingIds.Contains(x.PolicyId) && (!x.RestrictSelfServiceToDeviceTargets || deviceIds.Contains(x.PolicyId)))
                .Select(x => x.PolicyId).ToHashSet();
            return await ReadPublishedAsync(db, matchingIds, ct);
        }
        var result = devicePolicies.Where(x =>
            (!x.Policy.Settings.TryGetProperty("Trigger", out var trigger) || trigger.GetInt32() != (int)PolicyTrigger.SelfServiceOnly) &&
            (!automaticIds.Contains(x.Policy.Id) || !IsLogin(x) || matchingIds.Contains(x.Policy.Id))).ToList();
        var restricted = (await db.GroupPolicies.Select(x => x.PolicyId).Concat(db.DirectoryOuPolicies.Select(x => x.PolicyId)).Concat(db.DevicePolicies.Select(x => x.PolicyId)).Distinct().ToListAsync(ct)).ToHashSet();
        matchingIds.ExceptWith(restricted);
        result.AddRange((await ReadPublishedAsync(db, matchingIds, ct)).Where(IsLogin));
        return result.DistinctBy(x => x.Policy.Id).OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Id).ToList();
    }

    private static async Task<(HashSet<(int, Guid)> direct, HashSet<(int, Guid)> ancestors)> ReadUserMembershipAsync(CloneDeployDbContext db, string? sid, CancellationToken ct)
    {
        var users = string.IsNullOrWhiteSpace(sid) ? [] : (await DirectoryIdentity.UsersAsync(db, sid, ct)).Users;
        var direct = new HashSet<(int, Guid)>();
        var ancestors = new HashSet<(int, Guid)>();
        var groupSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            direct.Add((user.DirectorySourceId, user.ObjectGuid));
            var objects = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == user.DirectorySourceId && !x.IsMissing).ToDictionaryAsync(x => x.ObjectGuid, ct);
            var groups = await db.DirectoryEffectiveGroupMemberships.Where(x => x.DirectorySourceId == user.DirectorySourceId && x.MemberGuid == user.ObjectGuid).Select(x => x.GroupGuid).ToListAsync(ct);
            foreach (var group in groups)
                if (objects.TryGetValue(group, out var item) && item.Kind == DirectoryObjectKind.Group && !item.Disabled)
                {
                    direct.Add((user.DirectorySourceId, group));
                    if (!string.IsNullOrWhiteSpace(item.ObjectSid)) groupSids.Add(item.ObjectSid);
                }
            if (user.DirectorySource.Provider != DirectoryProvider.ActiveDirectory) continue;
            var parent = objects.GetValueOrDefault(user.ObjectGuid)?.ParentGuid;
            if (parent.HasValue && objects.ContainsKey(parent.Value)) direct.Add((user.DirectorySourceId, parent.Value));
            var seen = new HashSet<Guid>();
            while (parent is Guid id && seen.Add(id) && objects.TryGetValue(id, out var item))
            {
                ancestors.Add((user.DirectorySourceId, id));
                parent = item.ParentGuid;
            }
        }

        // A displayed AD group also represents its synchronized Entra group. This
        // includes cloud-only users who belong to that group only through Entra.
        if (groupSids.Count > 0)
        {
            var equivalents = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
                .Where(x => x.Kind == DirectoryObjectKind.Group && (x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly) &&
                    !x.IsMissing && !x.Disabled && x.ObjectSid != null && groupSids.Contains(x.ObjectSid)).ToListAsync(ct);
            foreach (var group in equivalents.GroupBy(x => x.ObjectSid))
            {
                if (group.GroupBy(x => x.DirectorySource.Provider).Any(x => x.Count() > 1)) continue;
                foreach (var item in group) direct.Add((item.DirectorySourceId, item.ObjectGuid));
            }
        }
        return (direct, ancestors);
    }

    private static bool IsLogin(CompiledPolicySnapshotDto snapshot) => snapshot.Policy.Settings.TryGetProperty("Trigger", out var value) && value.GetInt32() == (int)PolicyTrigger.Login;
    private static async Task<List<CompiledPolicySnapshotDto>> ReadPublishedAsync(CloneDeployDbContext db, HashSet<int> ids, CancellationToken ct)
    {
        var json = await db.ActiveCompiledPolicies.AsNoTracking().Where(x => ids.Contains(x.PolicyId) && !x.Policy!.Archived && x.Policy.ArchivedAtUtc == null).Select(x => x.Json).ToListAsync(ct);
        return json.Select(x => JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(x) ?? throw new InvalidDataException("Invalid policy snapshot."))
            .OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Id).ToList();
    }
}
