namespace CloneDeploy_Server.WinPe;

public sealed record SetupRequestDto(Guid Id, ToolchainSpecDto Toolchain, DateTime RequestedAtUtc)
{ public bool RemoteExecution { get; init; } }
