using System.Buffers.Binary;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class DirectoryIdentity
{
    // Names and email addresses are never identity keys. Only one record per provider
    // may participate in a shared SID identity; duplicates within a provider fail closed.
    internal static async Task<(string Status, List<DirectoryObject> Users)> UsersAsync(
        CloneDeployDbContext db, string sid, CancellationToken ct)
    {
        var cloudId = CloudObjectId(sid);
        var query = db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource)
            .Where(x => x.Kind == DirectoryObjectKind.User && (x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly));
        var users = await query.Where(x => x.ObjectSid == sid ||
            (cloudId != null && x.DirectorySource.Provider == DirectoryProvider.EntraId && x.ObjectGuid == cloudId))
            .Take(3).ToListAsync(ct);
        if (users.Count == 0) return ("NotFound", []);
        // An Entra Windows SID can resolve the cloud object, whose on-premises SID
        // then connects it to the AD representation.
        var sids = users.Select(x => x.ObjectSid).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (sids.Length > 1) return ("Ambiguous", []);
        if (sids.Length == 1 && sids[0] != sid)
            users = await query.Where(x => x.ObjectSid == sids[0]).Take(3).ToListAsync(ct);
        if (users.GroupBy(x => x.DirectorySource.Provider).Any(x => x.Count() > 1)) return ("Ambiguous", []);
        if (users.Any(x => x.Disabled && !x.IsMissing)) return ("Disabled", []);
        users = users.Where(x => !x.IsMissing).OrderBy(x => x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory ? 0 : 1).ToList();
        return users.Count == 0 ? ("Missing", []) : ("Matched", users);
    }

    internal static Dictionary<(int, Guid), DirectoryObject> PreferredObjects(IEnumerable<DirectoryObject> objects)
    {
        var rows = objects.ToList();
        var result = rows.ToDictionary(x => (x.DirectorySourceId, x.ObjectGuid));
        foreach (var group in rows.Where(x => x.Kind is DirectoryObjectKind.User or DirectoryObjectKind.Group &&
                     !string.IsNullOrWhiteSpace(x.ObjectSid)).GroupBy(x => (x.Kind, x.ObjectSid)))
        {
            // Disabled/missing sources don't hide a usable representation in the other directory.
            var candidates = group.Where(x => (x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly) && !x.IsMissing).ToList();
            if (candidates.GroupBy(x => x.DirectorySource.Provider).Any(x => x.Count() > 1)) continue;
            var preferred = candidates.OrderBy(x => x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory ? 0 : 1).FirstOrDefault();
            if (preferred is null) continue;
            foreach (var item in group) result[(item.DirectorySourceId, item.ObjectGuid)] = preferred;
        }
        return result;
    }

    private static Guid? CloudObjectId(string sid)
    {
        var parts = sid.Split('-');
        if (parts.Length != 8 || !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) return null;
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 4; i++)
        {
            if (!uint.TryParse(parts[i + 4], out var value)) return null;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i * 4, 4), value);
        }
        return new Guid(bytes);
    }
}
