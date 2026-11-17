namespace CloneDeploy_Agent.Contracts;

public enum AgentDeviceOperation { Services, Processes, StartService, StopService, RestartService, EndProcess, RenameComputer, SystemStatus, Reboot, Shutdown }

public sealed record AgentDeviceToolRequest(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc,
    AgentDeviceOperation Operation, int Offset = 0, string? ServiceName = null, int ProcessId = 0,
    long ProcessStartedUtcTicks = 0)
{
    public string? ComputerName { get; init; }
    public AgentPolicyCredential? DomainCredential { get; init; }
}

public sealed record AgentServiceRow(string Name, string DisplayName, string Status, string StartupType);
public sealed record AgentProcessRow(int Id, string Name, long? StartedUtcTicks, double? CpuSeconds, long? MemoryBytes);
public sealed record AgentSystemStatus(DateTimeOffset CollectedAtUtc, long UptimeSeconds, string[] LoggedOnUsers, string? UsersError = null);

public sealed record AgentDeviceToolReply(Guid RequestId, bool Success, string Detail,
    AgentServiceRow[] Services, AgentProcessRow[] Processes, bool HasMore = false)
{
    public AgentSystemStatus? SystemStatus { get; init; }
    public const int PageSize = 50;
    public static AgentDeviceToolReply Result(Guid id, bool success, string detail) => new(id, success, detail, [], []);
}
