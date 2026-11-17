using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyTargetOptionDto(PolicyTargetKeyDto Key, string Name, string Description, bool Available)
{
    public string GroupType { get; init; } = "";
    public string GroupDescription { get; init; } = "";
    public int DeviceCount { get; init; }
}