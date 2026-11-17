namespace CloneDeploy_Server.WinPe;

public sealed record SetupStatusDto(string State, string Message, DateTime UpdatedAtUtc)
{
    public bool IsComplete => State is "Succeeded" or "Failed" or "RestartRequired";
}
