using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectorySnapshotDto(Guid RootObjectGuid, IReadOnlyList<DirectoryObject> Objects)
{
    public DirectorySnapshotDto ForApplicationAccess()
    {
        var objects = Objects.Where(x => x.ObjectGuid == RootObjectGuid || x.Kind is
            CloneDeploy_Server.Backend.Enum.DirectoryObjectKind.User or CloneDeploy_Server.Backend.Enum.DirectoryObjectKind.Group).ToList();
        var ids = objects.Select(x => x.ObjectGuid).ToHashSet();
        return new(RootObjectGuid, objects) { Memberships = Memberships.Where(x => ids.Contains(x.GroupGuid) && (x.MemberGuid == null || ids.Contains(x.MemberGuid.Value))).ToList() };
    }
    public IReadOnlyList<DirectoryGroupMembership> Memberships { get; init; } = [];
}
