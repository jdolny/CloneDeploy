using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Matches imported directory computers with managed device records.
/// </summary>
public static class DirectoryDeviceReconciler
{
    // Entra display names are not evidence for attaching an existing managed identity.
    // Unique new names can seed enrollment records, just as an LDAP import does.
    public static DirectoryDeviceReconciliationDto ReconcileEntra(int sourceId,
        IReadOnlyList<DirectoryObject> objects, IReadOnlyList<Device> devices)
    {
        var computers = objects.Where(x => x.DirectorySourceId == sourceId &&
            x.Kind == DirectoryObjectKind.Computer && !x.IsMissing && !x.IsScopeAncestor).ToList();
        var linked = devices.Where(x => x.DirectorySourceId == sourceId && x.DirectoryObjectGuid != null)
            .Select(x => x.DirectoryObjectGuid!.Value).ToHashSet();
        var existingNames = devices.Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = computers.ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var additions = new List<Device>();
        var conflicts = new List<string>();
        foreach (var computer in computers)
        {
            if (linked.Contains(computer.ObjectGuid)) continue;
            var name = computer.Name.Trim();
            if (name.Length is < 1 or > 128 || name.Any(char.IsControl) || names[name].Count() != 1 || existingNames.Contains(name))
            {
                conflicts.Add(name.Length == 0 ? computer.ObjectGuid.ToString() : name);
                continue;
            }
            // Disabled cloud identities must not seed an enrollment/preprovision record.
            if (computer.Disabled) continue;
            additions.Add(new Device { Name = name, Guid = Guid.NewGuid(),
                DirectorySourceId = sourceId, DirectoryObjectGuid = computer.ObjectGuid });
            existingNames.Add(name);
            linked.Add(computer.ObjectGuid);
        }
        return new(additions, conflicts);
    }

    // Include archived devices: their links deliberately prevent rediscovery from restoring them.
    public static DirectoryDeviceReconciliationDto Reconcile(int directorySourceId,
        IReadOnlyList<DirectoryObject> objects, IReadOnlyList<Device> devices)
    {
        var computers = objects.Where(x => x.DirectorySourceId == directorySourceId &&
            x.Kind == DirectoryObjectKind.Computer && !x.IsMissing && !x.IsScopeAncestor).ToList();
        var linked = devices.Where(x => x.DirectorySourceId == directorySourceId && x.DirectoryObjectGuid != null)
            .Select(x => x.DirectoryObjectGuid!.Value).ToHashSet();
        var byName = devices.Where(x => x.DirectorySourceId == null && x.DirectoryObjectGuid == null)
            .ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var directoryNames = computers.ToLookup(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var additions = new List<Device>();
        var conflicts = new List<string>();
        foreach (var computer in computers)
        {
            if (linked.Contains(computer.ObjectGuid)) continue;
            var name = computer.Name.Trim();
            if (name.Length == 0)
            {
                conflicts.Add(computer.ObjectGuid.ToString());
                continue;
            }
            var candidates = byName[name].ToList();
            if (candidates.Count > 0)
            {
                // A hostname alone isn't enough evidence to attach an existing managed device.
                var candidate = candidates.Count == 1 ? candidates[0] : null;
                var domain = candidate?.ComputerSystem?.Domain?.Trim().TrimEnd('.');
                if (candidate is null || candidate.DirectorySourceId != null || candidate.DirectoryObjectGuid != null ||
                    directoryNames[name].Count() != 1 || string.IsNullOrWhiteSpace(domain) ||
                    !string.Equals(computer.DnsHostName?.TrimEnd('.'), $"{name}.{domain}", StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(name);
                    continue;
                }
                candidate.DirectorySourceId = directorySourceId;
                candidate.DirectoryObjectGuid = computer.ObjectGuid;
            }
            else
            {
                additions.Add(new Device
                {
                    Name = name,
                    Guid = Guid.NewGuid(),
                    DirectorySourceId = directorySourceId,
                    DirectoryObjectGuid = computer.ObjectGuid
                });
            }
            linked.Add(computer.ObjectGuid);
        }
        return new DirectoryDeviceReconciliationDto(additions, conflicts);
    }
}
