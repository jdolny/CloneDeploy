namespace CloneDeploy_Agent.Contracts;

/// <summary>Identifies a prepared Windows agent release; download URLs stay on the enrolled server.</summary>
public sealed record AgentUpdateOffer(string Version, string Sha256, long Length)
{
    private const int MaximumReleaseNumber = 65534;

    public string Architecture { get; init; } = "x64";
    public const long MaximumLength = 1024L * 1024 * 1024;
    public static string DisplayVersion(string? value) => System.Version.TryParse(value, out var version) && version.Major == 2 && version.Minor == 0 &&
        version.Build is >= 1 and <= MaximumReleaseNumber && version.Revision <= 0
        ? version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
    public static System.Version ParseVersion(string value)
    {
        if (!System.Version.TryParse(value, out var version) || version.Major != 2 || version.Minor != 0 ||
            version.Build is < 1 or > MaximumReleaseNumber || version.Revision > 0)
            throw new InvalidDataException("Agent releases require Telos version 2.0.N (release N from 1 to 65534).");
        return new(version.Major, version.Minor, version.Build);
    }
    public void Validate()
    {
        _ = ParseVersion(Version);
        _ = WindowsArchitecture.Require(Architecture);
        if (Sha256 is null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit) || Length is <= 0 or > MaximumLength)
            throw new InvalidDataException("Invalid agent update metadata.");
    }
    public bool IsNewerThan(string installed)
    {
        Validate();
        return ParseVersion(Version) > ParseVersion(installed);
    }
}
