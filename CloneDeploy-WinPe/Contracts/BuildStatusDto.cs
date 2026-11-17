namespace CloneDeploy_Server.WinPe;

public sealed record BuildStatusDto(string State, string Message, DateTime UpdatedAtUtc, List<BuildArtifactDto> Artifacts)
{
    public bool IsComplete => State is "Ready" or "Failed" or "Cancelled" or "BootPreparationFailed";
}
