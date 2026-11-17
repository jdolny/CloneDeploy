using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the counts, recent activity, and status information shown on the dashboard.
/// </summary>
public class DashboardSnapshotDto
{
    public DateTime AsOfUtc { get; set; }
    public int Days { get; set; }
    public bool CanViewDevices { get; set; }
    public bool CanApprove { get; set; }
    public bool CanViewTasks { get; set; }
    public bool CanViewAllTasks { get; set; }
    public int? TaskUserId { get; set; }
    public bool CanViewUpdates { get; set; }
    public bool IsAdmin { get; set; }
    public int DeviceCount { get; set; }
    public int OverdueCount { get; set; }
    public int PendingApprovals { get; set; }
    public int FailedCount { get; set; }
    public DashboardFleetDto? Fleet { get; set; }
    public List<DashboardAttentionDto> Attention { get; set; } = [];
    public List<DashboardDeviceAgeDto> DeviceAges { get; set; } = [];
    public List<DashboardDayDto> Results { get; set; } = [];
    public List<DashboardDeploymentDto> Deployments { get; set; } = [];
    public List<DashboardScheduledTaskDto> Scheduler { get; set; } = [];
}
