using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public record DashboardDeploymentDto(Guid Id, string Name, string RequestedBy, DateTime CreatedAtUtc, int Total, int Succeeded, int Failed, int Cancelled, int Unknown);
