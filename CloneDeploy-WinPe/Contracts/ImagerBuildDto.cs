namespace CloneDeploy_Server.WinPe;

// Frozen public connection settings for the download-on-boot bootstrap.
public sealed record ImagerBuildDto(string? ServerUrl)
{
    public string? TrustedRootCertificate { get; init; }
    public string[] TimeServers { get; init; } = ["time.windows.com"];
}
