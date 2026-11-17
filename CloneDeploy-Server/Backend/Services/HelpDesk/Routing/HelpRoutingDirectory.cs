using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpDirectoryChoice(string Key, string Label);

public static class HelpRoutingDirectory
{
    public static string Key(int source, Guid guid) => $"{source}:{guid:D}";
    public static bool TryKey(string? value, out int source, out Guid guid)
    {
        source = 0; guid = Guid.Empty;
        var parts = value?.Split(':');
        return parts?.Length == 2 && int.TryParse(parts[0], out source) && source > 0 && Guid.TryParse(parts[1], out guid);
    }
    public static IQueryable<DirectoryObject> Active(CloneDeployDbContext db) => db.DirectoryObjects.AsNoTracking()
        .Where(x => !x.IsMissing && !x.Disabled && x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly);

    public static async Task<HelpRoutingInput> PopulateAsync(CloneDeployDbContext db, HelpRoutingInput input,
        IEnumerable<DirectoryObject> users, IEnumerable<DirectoryObject> devices, CancellationToken ct)
    {
        var userGroups = new HashSet<string>(); var userOus = new HashSet<string>();
        var deviceGroups = new HashSet<string>(); var deviceOus = new HashSet<string>();
        await Collect(users, userGroups, userOus);
        await Collect(devices, deviceGroups, deviceOus);
        return input with { UserGroups = userGroups, UserOus = userOus, DeviceGroups = deviceGroups, DeviceOus = deviceOus };

        async Task Collect(IEnumerable<DirectoryObject> subjects, HashSet<string> groups, HashSet<string> ous)
        {
            foreach (var source in subjects.GroupBy(x => x.DirectorySourceId))
            {
                var ids = source.Select(x => x.ObjectGuid).ToArray();
                var groupIds = await Active(db).Where(x => x.DirectorySourceId == source.Key && x.Kind == DirectoryObjectKind.Group &&
                    db.DirectoryEffectiveGroupMemberships.Any(m => m.DirectorySourceId == source.Key && m.GroupGuid == x.ObjectGuid && ids.Contains(m.MemberGuid)))
                    .Select(x => x.ObjectGuid).ToArrayAsync(ct);
                foreach (var id in groupIds) groups.Add(Key(source.Key, id));
                // Walk GUID parents, not DN suffixes (which can contain escaped commas).
                var parents = await Active(db).Where(x => x.DirectorySourceId == source.Key &&
                    (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Container || x.Kind == DirectoryObjectKind.Domain))
                    .ToDictionaryAsync(x => x.ObjectGuid, ct);
                foreach (var subject in source)
                {
                    var visited = new HashSet<Guid>();
                    var parent = subject.ParentGuid;
                    while (parent is { } id && visited.Add(id) && parents.TryGetValue(id, out var row))
                    {
                        if (row.Kind == DirectoryObjectKind.OrganizationalUnit) ous.Add(Key(source.Key, id));
                        parent = row.ParentGuid;
                    }
                }
            }
        }
    }
}
