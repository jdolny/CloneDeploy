namespace CloneDeploy_Server.WinPe;

/// <summary>Standalone boot outputs from one x64 WinPE build.</summary>
public static class WinPeBootFiles
{
    public static IReadOnlyList<string> VariantOutputs { get; } = Array.AsReadOnly(new[]
    {
        "winpe-x64.iso", "boot.wim", "BCD", "boot.sdi", "bootmgr", "bootmgfw.efi",
        "bootmgr.exe", "pxeboot.com", "pxeboot.n12"
    });

    public static IReadOnlyList<string> Arm64Outputs { get; } = Array.AsReadOnly(new[]
    {
        "winpe-arm64.iso", "boot-arm64.wim", "BCD-arm64", "boot-arm64.sdi", "bootaa64.efi"
    });

    public static IEnumerable<string> RequiredOutputs(BuildRequestDto request) =>
        (request.IsBase ? new[] { "base-media.zip" } : VariantOutputs)
        .Concat(request.BuildArm64 ? (request.IsBase ? new[] { "base-media-arm64.zip" } : Arm64Outputs) : [])
        .Append("manifest.json");

    public static async Task CopyArm64MediaAsync(string media, string destination, CancellationToken ct)
    {
        foreach (var (source, name) in new[]
        {
            ("sources/boot.wim", "boot-arm64.wim"), ("EFI/Microsoft/Boot/BCD", "BCD-arm64"),
            ("Boot/boot.sdi", "boot-arm64.sdi"), ("EFI/Boot/bootaa64.efi", "bootaa64.efi")
        })
            await CopyAsync(Path.Combine(media, source), destination, name, ct);
    }

    public static IReadOnlyList<string> NativeOutputs { get; } = Array.AsReadOnly(new[] { "native-bios.bcd", "native-uefi.bcd" });
    public static IReadOnlyList<string> Arm64NativeOutputs { get; } = Array.AsReadOnly(new[] { "native-arm64.bcd" });

    // These files must be exported while the customized WIM is still mounted.
    public static async Task CopyLegacyAsync(string mount, string destination, CancellationToken ct)
    {
        foreach (var name in new[] { "bootmgr.exe", "pxeboot.com", "pxeboot.n12" })
            await CopyAsync(Path.Combine(mount, "Windows", "Boot", "PXE", name), destination, name, ct);
    }

    // Called after MakeWinPEMedia so /bootex's selected EFI manager is preserved.
    public static async Task CopyMediaAsync(string media, string destination, CancellationToken ct)
    {
        foreach (var (source, name) in new[]
        {
            ("sources/boot.wim", "boot.wim"), ("Boot/BCD", "BCD"),
            ("Boot/boot.sdi", "boot.sdi"), ("bootmgr", "bootmgr"),
            ("EFI/Boot/bootx64.efi", "bootmgfw.efi")
        })
            await CopyAsync(Path.Combine(media, source), destination, name, ct);
    }

    private static async Task CopyAsync(string source, string destination, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BuildStorage.RejectLinks(source);
        var target = Path.Combine(destination, name);
        BuildStorage.RejectLinks(target);
        if (!File.Exists(source) || new FileInfo(source).Length == 0)
            throw new InvalidDataException("Required WinPE boot source is missing or empty: " + source);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
    }
}
