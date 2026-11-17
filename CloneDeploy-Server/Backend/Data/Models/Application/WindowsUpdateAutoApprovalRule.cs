namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>A global approval rule; populated criteria are combined with AND, selections within each criterion with OR.</summary>
public class WindowsUpdateAutoApprovalRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string[] Severities { get; set; } = [];
    public string[] ClassificationIds { get; set; } = [];
    public int DelayDays { get; set; }
    public int AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
}
