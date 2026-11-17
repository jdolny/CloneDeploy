using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Describes a policy assigned to a group or directory location and its running order.
/// </summary>
public sealed class TargetPolicyAssignmentDto
{
    public int PolicyId { get; set; }
    public string Name { get; set; } = "";
    public int ExecutionPriority { get; set; } = 100;
    public int Order { get; set; }
    public bool IncludeDescendants { get; set; } = true;
}