using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the available and assigned policies for a group or directory location.
/// </summary>
public sealed class TargetPolicyStateDto
{
    public required PolicyTargetKeyDto Key { get; set; }
    public string Version { get; set; } = "";
    public List<TargetPolicyOptionDto> AvailablePolicies { get; set; } = [];
    public List<TargetPolicyAssignmentDto> Assignments { get; set; } = [];
}