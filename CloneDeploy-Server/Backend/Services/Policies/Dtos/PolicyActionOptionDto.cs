using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyActionOptionDto(Guid Guid, int Id, string Name, ActionType Type)
{
    public string Url => $"/actions/{Type.ToString().ToLowerInvariant()}/{Id}";
}