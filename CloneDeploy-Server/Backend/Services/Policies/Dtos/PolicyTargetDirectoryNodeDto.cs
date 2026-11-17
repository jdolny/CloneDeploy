using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyTargetDirectoryNodeDto(
    int SourceId,
    string SourceName,
    Guid Guid,
    Guid? ParentGuid,
    string Name,
    string Path,
    bool IsOu,
    bool Available);