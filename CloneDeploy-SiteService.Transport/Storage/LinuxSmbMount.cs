namespace CloneDeploy.SiteServices;

// Linux owns mounting and credentials. Never accept an ordinary directory as an SMB mount.
public static class LinuxSmbMount
{
    public static void Verify(string? path, string unc)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException("OS-managed SMB mount paths require Linux.");
        if (path is null || !Matches(path, unc, File.ReadAllLines("/proc/self/mountinfo")))
            throw new IOException("The Linux storage path is not mounted read/write from the configured SMB share.");
        // A symlink could escape the mount we just verified.
        for (var part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linux SMB storage paths cannot contain symbolic links.");
    }

    // Pure parser so missing mounts, overmounts and mismatched shares can be tested on any OS.
    public static bool Matches(string path, string unc, IEnumerable<string> mountInfo)
    {
        if (!path.StartsWith('/') || path.Contains('\\') || path.Any(char.IsControl) ||
            path.Split('/').Any(x => x is "." or "..") || !unc.StartsWith(@"\\") || unc.StartsWith(@"\\?\") || unc.StartsWith(@"\\.\")) return false;
        path = path.TrimEnd('/');
        string[]? selected = null; int separator = -1; string mount = "";
        foreach (var line in mountInfo)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var split = Array.IndexOf(fields, "-");
            if (split < 6 || fields.Length < split + 4) continue;
            var candidate = Decode(fields[4]).TrimEnd('/');
            if ((path == candidate || path.StartsWith(candidate + "/", StringComparison.Ordinal)) && candidate.Length >= mount.Length)
            { selected = fields; separator = split; mount = candidate; }
        }
        if (selected is null || selected[separator + 1] is not ("cifs" or "smb3") ||
            !selected[5].Split(',').Contains("rw") || !selected[separator + 3].Split(',').Contains("rw")) return false;
        var source = Decode(selected[separator + 2]).Replace('\\', '/').TrimEnd('/');
        var root = Decode(selected[3]).Trim('/');
        var prefix = selected[separator + 3].Split(',').FirstOrDefault(x => x.StartsWith("prefixpath="));
        if (root.Length == 0 && prefix is not null) root = Decode(prefix[11..]).Trim('/');
        var actual = source + (root.Length == 0 ? "" : "/" + root) + path[mount.Length..];
        // Server and share names are case-insensitive; subdirectories may be case-sensitive on the NAS.
        var expectedParts = unc.Replace('\\', '/').TrimEnd('/').Split('/');
        var actualParts = actual.Split('/');
        return expectedParts.Length == actualParts.Length && expectedParts.Select((part, index) =>
            string.Equals(part, actualParts[index], index < 4 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).All(x => x);
    }

    private static string Decode(string value) => value.Replace(@"\040", " ").Replace(@"\011", "\t").Replace(@"\012", "\n").Replace(@"\134", "\\");
}
