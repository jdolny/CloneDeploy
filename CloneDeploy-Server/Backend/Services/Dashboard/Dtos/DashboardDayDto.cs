using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public record DashboardDayDto(DateTime Date, int Succeeded, int Failed);
