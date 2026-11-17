using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceDirectoryRelation(string Kind, string Name, string Details);

public sealed class DeviceDirectoryRelations(ICloneDeployDbFactory factory, IAuthorizationService authorization)
{
    public async Task<List<DeviceDirectoryRelation>> ReadAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct);
        if (device?.DirectorySourceId is not { } source || device.DirectoryObjectGuid is not { } guid) return [];
        var subject = await HelpRoutingDirectory.Active(db).SingleOrDefaultAsync(x => x.DirectorySourceId == source &&
            x.ObjectGuid == guid && x.Kind == DirectoryObjectKind.Computer, ct);
        if (subject is null) return [];
        var membership = await HelpRoutingDirectory.PopulateAsync(db, new("", "", "", []), [], [subject], ct);
        var ids = membership.DeviceGroups.Concat(membership.DeviceOus).Select(key => Guid.Parse(key.Split(':')[1])).ToArray();
        var objects = await HelpRoutingDirectory.Active(db).Include(x => x.DirectorySource)
            .Where(x => x.DirectorySourceId == source && ids.Contains(x.ObjectGuid)).OrderBy(x => x.Name).ToListAsync(ct);
        var directGroups = await db.DirectoryGroupMemberships.Where(x => x.DirectorySourceId == source && x.MemberGuid == guid)
            .Select(x => x.GroupGuid).ToListAsync(ct);
        return objects.Select(x => new DeviceDirectoryRelation(
            x.Kind == DirectoryObjectKind.Group ? "Directory groups" : "Directory OUs", x.Name,
            (x.Kind == DirectoryObjectKind.Group ? directGroups.Contains(x.ObjectGuid) ? UiText.Get(LocKeys.Ui.DeviceDirectoryRelations.DirectMembership) : UiText.Get(LocKeys.Ui.DeviceDirectoryRelations.NestedMembership)
                : x.ObjectGuid == subject.ParentGuid ? UiText.Get(LocKeys.Ui.DeviceDirectoryRelations.ParentOU) : UiText.Get(LocKeys.Ui.DeviceDirectoryRelations.AncestorOU)) + $" · {x.DirectorySource.Name} · {x.DistinguishedName}")).ToList();
    }
}
