using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record TargetPolicyOptionDto(int Id, string Name)
{
    public int ExecutionPriority { get; init; } = 100;
}