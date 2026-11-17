namespace CloneDeploy.SiteServices;

public static class SiteBootPaths
{
    private const int MaximumRelativePathCharacters = 512;

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > MaximumRelativePathCharacters || relative.Contains('\\') || relative.Contains(':') ||
            relative.Any(char.IsControl) || relative.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw new InvalidDataException("Invalid boot file path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid boot file path.");
        RejectLinks(path);
        return path;
    }
    public static void RejectLinks(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Boot files cannot use symbolic links or junctions.");
    }
}
