namespace CloneDeploy_Server.Backend.Services;

public sealed record PxeBootFilesDto(string IpxeVersion, string WimbootVersion, string ServerUrl,
    DateTime PreparedAtUtc, string IpxeSha256, string WimbootSha256)
{
    public string? Arm64WimbootSha256 { get; init; }
}
