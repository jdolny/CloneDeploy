namespace CloneDeploy_Server.WinPe;

public static class UdpcastBundle
{
    public static async Task InstallAsync(string mount, bool arm64, CancellationToken ct)
    {
        var assembly = typeof(UdpcastBundle).Assembly;
        const string prefix = "CloneDeploy.Udpcast.";
        var architecture = arm64 ? "a64" : "x64";
        var directory = Path.Combine(mount, "CloneDeploy", "Tools", "udpcast");
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
                !name.StartsWith(prefix + "x64.", StringComparison.Ordinal) &&
                !name.StartsWith(prefix + "a64.", StringComparison.Ordinal))
            .Append(prefix + architecture + ".udp-receiver.exe");
        foreach (var resource in resources)
        {
            var name = resource[prefix.Length..];
            var path = name == architecture + ".udp-receiver.exe"
                ? Path.Combine(directory, architecture, "udp-receiver.exe")
                : Path.Combine(directory, name);
            BuildStorage.RejectLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException("Missing bundled UDPcast resource: " + resource);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }
    }
}
