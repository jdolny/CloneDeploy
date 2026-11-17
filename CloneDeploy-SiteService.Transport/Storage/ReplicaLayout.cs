namespace CloneDeploy.SiteServices;

public static class ReplicaLayout
{
    private const int MaximumExtensionCharacters = 16;
    private const int MaximumStemCharacters = 100;
    private const int MaximumRelativePathCharacters = 400;

    // File revisions share a folder, but never a final filename or partial download.
    public static string Shared(string kind, string logicalKey, string name, string id)
    {
        if (id.Length != SiteProxyProtocol.Sha256HexCharacters || !id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid shared content identity.");
        var folder = kind switch
        {
            "WinPe" => "winpe",
            "BootFile" => "tftp",
            "DriverArchive" => "prepared-drivers",
            "DriverOriginal" => "drivers",
            "ManagementFile" => "actions",
            _ => throw new InvalidDataException("Invalid replica kind.")
        };
        if (kind is "WinPe" or "BootFile")
        {
            ValidateRelative(logicalKey);
            var slash = logicalKey.LastIndexOf('/');
            if (slash >= 0) folder += "/" + logicalKey[..slash];
            name = logicalKey[(slash + 1)..];
        }
        // Display names may contain characters unsuitable for a portable filename.
        name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "file";
        var extension = kind == "DriverArchive" ? ".zip" : Path.GetExtension(name);
        if (extension.Length > MaximumExtensionCharacters) extension = "";
        var stem = kind == "DriverArchive" ? name : name[..(name.Length - extension.Length)];
        stem = stem[..Math.Min(MaximumStemCharacters, stem.Length)];
        return $"{folder}/{stem}--{id.ToLowerInvariant()}{extension}";
    }

    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumRelativePathCharacters || path.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c)) ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Invalid replica relative path.");
    }
}
