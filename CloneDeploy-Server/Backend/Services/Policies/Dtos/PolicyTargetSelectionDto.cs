using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds a selected policy destination, its order, and whether to include locations beneath it.
/// </summary>
public sealed class PolicyTargetSelectionDto
{
    public required PolicyTargetKeyDto Key { get; set; }
    public bool IncludeDescendants { get; set; } = true;
    public int Order { get; set; }
}