using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public record DashboardDeviceDto(int Id, string Name, DateTime? LastCheckin, string? AgentVersion);
