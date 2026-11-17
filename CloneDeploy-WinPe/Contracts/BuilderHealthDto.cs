namespace CloneDeploy_Server.WinPe;

public sealed record BuilderHealthDto(DateTime SeenAtUtc, bool Ready, string Message, string? AdkVersion);
