using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the available and selected destinations when editing where a policy applies.
/// </summary>
public sealed class PolicyTargetStateDto
{
    public string AssignmentsVersion { get; set; } = "";
    public int PolicyId { get; set; }
    public PolicyTrigger Trigger { get; set; }
    public bool NotificationAudienceAllowed { get; set; }
    public bool SaveLoginAudience { get; set; }
    public int? LoginAudienceId { get; set; }
    public Guid AudienceRevision { get; set; }
    public List<UserAudience> Audiences { get; set; } = [];
    public string Version { get; set; } = "";
    public bool Archived { get; set; }
    public bool Active { get; set; }
    public List<PolicyTargetOptionDto> Options { get; set; } = [];
    public List<PolicyTargetDirectoryNodeDto> DirectoryNodes { get; set; } = [];
    public bool CanSelectOus { get; set; }
    public List<PolicyTargetSelectionDto> Selected { get; set; } = [];
    public int? ConditionId { get; set; }
    public PolicyConditionFailedAction ConditionFailedAction { get; set; }
    public List<PolicyConditionOptionDto> Conditions { get; set; } = [];
}
