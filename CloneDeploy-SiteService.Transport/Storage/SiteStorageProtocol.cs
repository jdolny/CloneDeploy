namespace CloneDeploy.SiteServices;

public static class SiteStorageProtocol
{
    private const string SharedKeyPrefix = "shared-";
    private const string ImageKeyPrefix = "image-";
    private const int CompactGuidCharacters = 32;

    public const string CapabilityHeader = "X-CloneDeploy-Storage";
    public const string BypassHeader = "X-CloneDeploy-Storage-Bypass";
    public const string OfferHeader = "X-CloneDeploy-Storage-Offer";
    public static bool ValidKey(string key) => key is not null &&
        (key.StartsWith(SharedKeyPrefix) && key.Length == SharedKeyPrefix.Length + SiteProxyProtocol.Sha256HexCharacters && key[SharedKeyPrefix.Length..].All(Uri.IsHexDigit) ||
         key.StartsWith(ImageKeyPrefix) && key.Length == ImageKeyPrefix.Length + CompactGuidCharacters && Guid.TryParseExact(key[ImageKeyPrefix.Length..], "N", out _));

    public static string Resolve(string root, Guid site, int provider, string key, bool smb = false, string? relativePath = null)
    {
        if (site == Guid.Empty || provider <= 0 || !ValidKey(key) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\?\") || root.StartsWith(@"\\.\") ||
            root.StartsWith(@"\\") != smb || smb && !OperatingSystem.IsWindows())
            throw new InvalidDataException("Invalid replica destination.");
        relativePath ??= key.StartsWith(ImageKeyPrefix) ? $"images/{key[ImageKeyPrefix.Length..]}/image.wim" : throw new InvalidDataException("Missing replica path.");
        ValidatePath(key, relativePath);
        return SiteBootPaths.Resolve(root, $"replicas/site/{site:N}/{provider}/{relativePath}");
    }

    public static void ValidatePath(string key, string relativePath)
    {
        if (!ValidKey(key)) throw new InvalidDataException("Invalid replica identity.");
        ReplicaLayout.ValidateRelative(relativePath);
        if (key.StartsWith(SharedKeyPrefix))
        {
            var file = Path.GetFileName(relativePath);
            var suffix = "--" + key[SharedKeyPrefix.Length..].ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!(file.EndsWith(suffix, StringComparison.Ordinal) || stem.EndsWith(suffix, StringComparison.Ordinal)) ||
                relativePath.Split('/')[0] is not ("winpe" or "tftp" or "drivers" or "prepared-drivers" or "actions"))
                throw new InvalidDataException("Replica path does not match its identity.");
        }
        else if (relativePath != $"images/{key[ImageKeyPrefix.Length..]}/image.wim" && relativePath != $"images/{key[ImageKeyPrefix.Length..]}/image.ffu")
            throw new InvalidDataException("Invalid image replica path.");
    }
}
