namespace CloneDeploy_Server.WinPe;

public sealed record SetupHealthDto(DateTime SeenAtUtc, int Protocol, string? AdkVersion, string? WinPeVersion, bool Elevated, bool RestartRequired)
{
    public string? RequiredVersion { get; init; }
}
