namespace CloneDeploy_Server.Backend.Services;

public sealed record DashboardFleetCount(string Bucket, int Count);
public sealed record DashboardFleetDto(int Devices, string? PreparedVersion,
    List<DashboardFleetCount> Updates, List<DashboardFleetCount> Agents);
public sealed record DashboardFleetDeviceDto(int Id, string Name, string? AgentVersion,
    DateTime? LastCheckin, DateTime? LastOnlineScan, bool? RebootRequired);
