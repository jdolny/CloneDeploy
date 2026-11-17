using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record EffectivePolicySourceDto(string Assignment, string Location);
public sealed record EffectivePolicyDetailsDto(CompiledPolicySnapshotDto Snapshot, List<EffectivePolicySourceDto> Sources)
{
    public bool MatchesTrigger(PolicyTrigger? trigger)
    {
        if (trigger is null) return true;
        // Match the agent's default for legacy snapshots without a numeric trigger.
        var settings = Snapshot.Policy.Settings;
        var configured = settings.TryGetProperty("Trigger", out var value) && value.ValueKind == JsonValueKind.Number
            ? (PolicyTrigger)value.GetInt32() : PolicyTrigger.StartupOrCheckin;
        return configured == trigger || configured == PolicyTrigger.StartupOrCheckin &&
            trigger is PolicyTrigger.Startup or PolicyTrigger.Checkin;
    }
}
