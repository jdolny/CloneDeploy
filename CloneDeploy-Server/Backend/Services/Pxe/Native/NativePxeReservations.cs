using System.Collections.Frozen;

namespace CloneDeploy_Server.Backend.Services;

public sealed record NativePxeTarget(int DefinitionId, Guid BuildId)
{
    public string Architecture { get; init; } = "x64";
    public string Prefix => $"WinPe/{BuildId:N}";
    public string Loader(bool efi) => Prefix + (Architecture == "arm64" ? "/arm64/bootmgfw.efi" : efi ? "/uefi/bootmgfw.efi" : "/bios/pxeboot.n12");
    public string Bcd(bool efi) => "\\" + (Prefix + (Architecture == "arm64" ? "/arm64/BCD" : efi ? "/uefi/BCD" : "/bios/BCD")).Replace('/', '\\');
}

/// <summary>Immutable packet-path snapshot: no database, file access, or matching on the UDP thread.</summary>
public sealed class NativePxeReservations
{
    public sealed record BootSnapshot(FrozenDictionary<string, NativePxeTarget> Targets,
        NativePxeTarget? Default, NativePxeTarget? Arm64Default, FrozenSet<string> KnownMacs)
    {
        public FrozenDictionary<string, string> Reasons { get; init; } = FrozenDictionary<string, string>.Empty;
    }
    public void SetDecisionReasons(Dictionary<string, string> reasons) =>
        Volatile.Write(ref _snapshot, ReadSnapshot() with { Reasons = reasons.ToFrozenDictionary() });
    public string Explain(string mac, bool arm64)
    {
        var snapshot = ReadSnapshot();
        return snapshot.Reasons.GetValueOrDefault(mac + (arm64 ? "|arm64" : ""))
            ?? (snapshot.Targets.ContainsKey(mac + (arm64 ? "|arm64" : "")) ? "Client has a ready native reservation."
                : !snapshot.KnownMacs.Contains(mac) && (arm64 ? snapshot.Arm64Default : snapshot.Default) is not null ? "Unknown client uses the ready native default."
                : snapshot.KnownMacs.Contains(mac) ? "Known client has no ready native selection for this architecture."
                : "Unknown client has no ready native default for this architecture.");
    }
    private BootSnapshot _snapshot = new(FrozenDictionary<string, NativePxeTarget>.Empty, null, null, FrozenSet<string>.Empty);
    public BootSnapshot ReadSnapshot() => Volatile.Read(ref _snapshot);
    public KeyValuePair<string, NativePxeTarget>[] Snapshot() => ReadSnapshot().Targets.Select(x => new KeyValuePair<string, NativePxeTarget>(x.Key.Split('|')[0], x.Value)).ToArray();
    public int Count => ReadSnapshot().Targets.Count;
    public string Status { get; private set; } = "Loading native PXE reservations.";
    public static string? Normalize(string? mac)
    {
        if (mac is null) return null;
        var value = mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpperInvariant();
        if (value.Length != 12 || !value.All(Uri.IsHexDigit) || value == "000000000000" ||
            (Convert.ToByte(value[..2], 16) & 1) != 0) return null;
        return value;
    }
    public NativePxeTarget? Find(byte hardwareType, byte length, byte[] address)
    {
        if (hardwareType != 1 || length != 6 || address.Length < 6) return null;
        var mac = Convert.ToHexString(address.AsSpan(0, 6));
        if (Normalize(mac) is null) return null;
        var snapshot = ReadSnapshot();
        return snapshot.Targets.GetValueOrDefault(mac) ?? (snapshot.KnownMacs.Contains(mac) ? null : snapshot.Default);
    }
    public void Replace(IEnumerable<(int DeviceId, string Mac, NativePxeTarget Target)> rows, string status)
        => ReplaceWithDefaults(rows, status, null, null, []);

    public static NativePxeTarget? ResolveSelection(IEnumerable<NativePxeTarget?> selections, NativePxeTarget? fallback, bool duplicate)
    {
        var targets = selections.Where(x => x is not null).Distinct().ToArray();
        return targets.Length == 1 ? targets[0] : duplicate ? fallback : null;
    }

    public void ReplaceWithDefaults(IEnumerable<(int DeviceId, string Mac, NativePxeTarget Target)> rows, string status,
        NativePxeTarget? defaultTarget, NativePxeTarget? arm64Default, IEnumerable<string> knownMacs)
    {
        var groups = rows.GroupBy(x => x.Mac + (x.Target.Architecture == "arm64" ? "|arm64" : "")).ToArray();
        var duplicates = groups.Count(g => g.Select(x => x.Target).Distinct().Count() > 1);
        var next = groups.Select(g => (g.Key, Target: ResolveSelection(g.Select(x => x.Target),
                g.First().Target.Architecture == "arm64" ? arm64Default : defaultTarget, g.Select(x => x.DeviceId).Distinct().Count() > 1)))
            .Where(x => x.Target is not null).ToFrozenDictionary(x => x.Key, x => x.Target!);
        Volatile.Write(ref _snapshot, new BootSnapshot(next, defaultTarget, arm64Default,
            knownMacs.Concat(groups.Select(g => g.First().Mac)).ToFrozenSet()));
        Status = status + (duplicates == 0 ? "" : $" {duplicates} conflicting MAC selections resolved using the native default when available.");
    }
    public void ReportError() => Status = "Native PXE refresh failed; using the last snapshot. See server logs.";
    public NativePxeTarget? FindArm64(byte hardwareType, byte length, byte[] address)
    {
        if (hardwareType != 1 || length != 6 || address.Length < 6) return null;
        var mac = Convert.ToHexString(address.AsSpan(0, 6));
        if (Normalize(mac) is null) return null;
        var snapshot = ReadSnapshot();
        return snapshot.Targets.GetValueOrDefault(mac + "|arm64") ?? (snapshot.KnownMacs.Contains(mac) ? null : snapshot.Arm64Default);
    }
}
