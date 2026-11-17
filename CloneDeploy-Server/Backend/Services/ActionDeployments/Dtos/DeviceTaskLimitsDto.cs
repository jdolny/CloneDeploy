using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds limits on device activity and how often devices check in.
/// </summary>
public sealed class DeviceTaskLimitsDto
{
    public int EnrollmentRequestsPerMinute { get; set; } = AgentEnrollmentRateSettings.DefaultLimit;
    public int MaxConcurrentInventorySubmissions { get; set; } = 10;
    public int MaxConcurrentDownloads { get; set; } = 20;
    public int CheckinIntervalMinutes { get; set; } = 60;
    public int MaxConcurrentTasks { get; set; } = 20;
    public int MaxConcurrentCheckins { get; set; } = 100;
}
