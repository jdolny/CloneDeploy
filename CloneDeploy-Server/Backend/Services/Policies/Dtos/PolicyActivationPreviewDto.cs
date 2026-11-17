using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyActivationPreviewDto(string Hash, bool Changed, bool OnceOnly)
{
    public List<string> Warnings { get; init; } = [];
}
