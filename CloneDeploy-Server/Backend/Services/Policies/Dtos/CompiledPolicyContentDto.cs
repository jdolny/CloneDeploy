using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CompiledPolicyContentDto(
    int Id,
    Guid Guid,
    string Name,
    int ExecutionPriority,
    JsonElement Settings,
    JsonElement? StartWindow,
    JsonElement? EndWindow,
    CompiledPolicyActionDto? Condition,
    List<CompiledPolicyActionDto> Actions)
{
    // Omit zero so existing snapshots retain their hash until a rerun is requested.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long ExecutionRevision { get; init; }
}