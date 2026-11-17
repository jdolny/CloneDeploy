namespace CloneDeploy_Server.WinPe;

public static class WimlibBundle
{
    public static async Task InstallAsync(string mount, bool arm64, CancellationToken ct)
    {
        var assembly = typeof(WimlibBundle).Assembly;
        var prefix = "CloneDeploy.Wimlib." + (arm64 ? "a64." : "x64.");
        var resources = assembly.GetManifestResourceNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var required in new[] { "wimlib-imagex.exe", "libwim-15.dll", "COPYING.txt", "COPYING.GPLv3.txt", "COPYING.LGPL.txt", "COPYING.libdivsufsort-lite.txt" })
            if (!resources.Contains(prefix + required)) throw new InvalidDataException("Missing bundled wimlib resource: " + required);
        var directory = Path.Combine(mount, "CloneDeploy", "Tools", "wimlib");
        BuildStorage.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        foreach (var resource in resources.Append("CloneDeploy.Wimlib.wimlib-1.14.5.tar.gz"))
        {
            var name = resource.StartsWith(prefix, StringComparison.Ordinal) ? resource[prefix.Length..] : "wimlib-1.14.5.tar.gz";
            var path = Path.Combine(directory, name);
            BuildStorage.RejectLinks(path);
            await using var input = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Missing bundled wimlib resource: " + resource);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }
    }
}
