using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the details, editing choices, assignments, and history shown for a policy.
/// </summary>
public sealed class PolicyDetailsDto
{
    public required Policy Policy { get; init; }
    public bool Active { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public IReadOnlyCollection<int> SelectedTags { get; set; } = [];
    public List<ScriptAction> Conditions { get; set; } = [];
    public List<PolicyActionOptionDto> Actions { get; set; } = [];
    public List<GroupPolicy> Groups { get; set; } = [];
    public List<string> OrganizationalUnits { get; set; } = [];
    public List<PolicyHistory> ClientHistory { get; set; } = [];
    public List<PolicyHashHistory> PolicyHistory { get; set; } = [];
    public List<AuditLog> Audits { get; set; } = [];
}