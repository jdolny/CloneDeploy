namespace CloneDeploy_Server.WinPe;

public sealed record BuildRequestDto(
    Guid Id,
    bool IsBase,
    Guid? BaseBuildId,
    string? BaseSha256,
    bool IncludePowerShell,
    bool UseBootEx,
    List<BuildDriverDto> Drivers)
{
    // Older queued recipes retain their immutable base inputs; new variants prepare media on the builder.
    public bool PrepareMediaAutomatically { get; init; }
    public bool RemoteExecution { get; init; }
    public bool BuildArm64 { get; init; }
    public string? BaseArm64Sha256 { get; init; }
    public ImagerBuildDto? Imager { get; init; }
    public string? RequiredToolchainVersion { get; init; }
    public string KeyboardLayout { get; init; } = WinPeKeyboardLayouts.Default;
}
