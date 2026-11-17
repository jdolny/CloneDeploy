using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CompiledPolicyActionDto(
    Guid Guid,
    string Name,
    ActionType Type,
    int Order,
    JsonElement Settings,
    CompiledPolicyActionDto? Condition,
    PolicyConditionFailedAction ConditionFailedAction,
    Guid? ConditionNextAction,
    List<CompiledPolicyFileDto> Files);