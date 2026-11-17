namespace CloneDeploy_Agent.Contracts;

/// <summary>Reviewed, immutable WinPE artifacts for a one-time agent-initiated imaging boot.</summary>
public sealed record AgentBootFile(string Name, long Size, string Sha256);
public sealed record AgentImagingReview(int DeviceId, Guid DeviceGuid, string DeviceName, int ImageId,
    Guid ImageVersionId, string ImageName, int ProfileId, string ProfileName, Guid BuildId, string VariantName,
    string MatchReason, string ServerUrl, AgentBootFile[] Files)
{
    // Old persisted plans were exclusively x64.
    public string Architecture { get; init; } = "x64";
    public string SourceFile(string name) => WindowsArchitecture.Require(Architecture) == "arm64" ? name switch
    {
        "boot.wim" => "boot-arm64.wim",
        "boot.sdi" => "boot-arm64.sdi",
        _ => throw new InvalidDataException("Invalid WinPE boot file.")
    } : name is "boot.wim" or "boot.sdi" ? name : throw new InvalidDataException("Invalid WinPE boot file.");
}
public sealed record AgentImagingCommand(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc,
    string ServerUrl, Guid? DeploymentId = null)
{
    public string Architecture { get; init; } = "x64";
}
public sealed record AgentImagingReply(Guid RequestId, bool Success, string Detail);
public sealed record AgentBootProgress(string Stage, string Detail);
