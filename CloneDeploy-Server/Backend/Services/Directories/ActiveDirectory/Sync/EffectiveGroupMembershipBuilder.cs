using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds all members of directory groups, including members inherited through other groups.
/// </summary>
internal static class EffectiveGroupMembershipBuilder
{
    internal static IEnumerable<DirectoryEffectiveGroupMembership> Build(
        DirectorySnapshotDto snapshot, int directorySourceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var objects = snapshot.Objects.ToDictionary(x => x.ObjectGuid);
        if (objects.Values.Any(x => x.DirectorySourceId != directorySourceId || x.IsMissing))
            throw new InvalidOperationException("Effective memberships require a current snapshot from one directory.");
        var children = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var edge in snapshot.Memberships)
        {
            ct.ThrowIfCancellationRequested();
            if (edge.DirectorySourceId != directorySourceId || !objects.TryGetValue(edge.GroupGuid, out var group) ||
                group.Kind != DirectoryObjectKind.Group)
                throw new InvalidOperationException("A membership references an invalid source group.");
            if (edge.MemberGuid is not { } member) continue; // Preserve unresolved DNs only in the direct table.
            if (!objects.ContainsKey(member))
                throw new InvalidOperationException("A resolved member is absent from the current snapshot.");
            if (!children.TryGetValue(edge.GroupGuid, out var members))
                children[edge.GroupGuid] = members = [];
            members.Add(member);
        }
        foreach (var root in children.Keys)
        {
            ct.ThrowIfCancellationRequested();
            // Visiting each object once per root handles diamonds, cycles and self-links.
            var visited = new HashSet<Guid> { root };
            var pending = new Stack<Guid>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!children.TryGetValue(current, out var members)) continue;
                foreach (var member in members)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!visited.Add(member)) continue;
                    yield return new DirectoryEffectiveGroupMembership
                    {
                        DirectorySourceId = directorySourceId, GroupGuid = root, MemberGuid = member
                    };
                    if (objects[member].Kind == DirectoryObjectKind.Group) pending.Push(member);
                }
            }
        }
    }
}
