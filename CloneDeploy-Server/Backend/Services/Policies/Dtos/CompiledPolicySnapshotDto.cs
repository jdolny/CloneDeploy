using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CompiledPolicySnapshotDto(int SchemaVersion, string Hash, bool ReRunExisting, CompiledPolicyContentDto Policy)
{
    // Admission metadata, outside the hashed policy content; persisted with each run.
    public string[]? NotificationRecipientSids { get; init; }
}