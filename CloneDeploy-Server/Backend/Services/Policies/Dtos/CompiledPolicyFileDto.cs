using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CompiledPolicyFileDto(int Id, Guid DirectoryGuid, string Name, long Size, string Sha256Hash);