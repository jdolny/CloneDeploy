using System.Text.Json;
using System.Text.Json.Nodes;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Freezes eligible package updates for an authenticated run using that device's published policies.</summary>
internal static class ManagedWinGetUpdates
{
    internal static bool IsUpdater(CompiledPolicyActionDto action) => action.Type == ActionType.Software &&
        Number(action.Settings, "Type") == 2 && Number(action.Settings, "WinGetInstallType") == 3;

    internal static async Task<List<CompiledPolicyActionDto>> ReadAsync(CloneDeployDbContext db, int deviceId, Guid runId, bool task, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({runId.ToString()}, 0))", ct);
        var policyRun = task ? null : await db.Set<AgentPolicyRun>().SingleAsync(x => x.Id == runId && x.DeviceId == deviceId, ct);
        var deviceTask = task ? await db.DeviceTasks.SingleAsync(x => x.Id == runId && x.DeviceId == deviceId, ct) : null;
        var json = task ? deviceTask!.ManagedWinGetActionsJson : policyRun!.ManagedWinGetActionsJson;
        if (json is null)
        {
            var policies = await PolicyDelivery.ReadAsync(db, deviceId, ct);
            var actions = Select(policies);
            json = JsonSerializer.Serialize(actions);
            if (task) deviceTask!.ManagedWinGetActionsJson = json;
            else policyRun!.ManagedWinGetActionsJson = json;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return JsonSerializer.Deserialize<List<CompiledPolicyActionDto>>(json)!;
    }

    internal static List<CompiledPolicyActionDto> Select(IEnumerable<CompiledPolicySnapshotDto> policies)
    {
        var eligible = policies.SelectMany(x => x.Policy.Actions).Where(x => x.Type == ActionType.Software &&
            Number(x.Settings, "Type") == 2 && Number(x.Settings, "WinGetInstallType") == 0 &&
            x.Settings.TryGetProperty("KeepUpdated", out var keep) && keep.ValueKind == JsonValueKind.True);
        var result = new List<CompiledPolicyActionDto>();
        foreach (var group in eligible.GroupBy(x => x.Settings.GetProperty("PackageId").GetString()!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(x => x.Settings.GetRawText()).Distinct().Count() != 1)
                throw new PolicyCompilationException($"Conflicting managed update settings for {group.Key}. Use one installation action per package.");
            var action = group.First();
            var settings = JsonNode.Parse(action.Settings.GetRawText())!.AsObject();
            settings["WinGetInstallType"] = 2;
            settings["ExplicitWinGetOperation"] = true;
            settings["InstallLatest"] = true;
            settings["RedirectStdOut"] = true;
            settings["RedirectStdError"] = true;
            // The updater owns the download slot; do not acquire a second slot for each child.
            settings["UseDownloadQueue"] = false;
            result.Add(action with { Settings = JsonSerializer.SerializeToElement(settings), Condition = null, Files = [] });
        }
        return result;
    }

    internal static async Task<bool> AllowsCredentialAsync(CloneDeployDbContext db, int deviceId, Guid runId, bool task, int credentialId, CancellationToken ct)
    {
        var json = task
            ? await db.DeviceTasks.Where(x => x.Id == runId && x.DeviceId == deviceId).Select(x => x.ManagedWinGetActionsJson).SingleOrDefaultAsync(ct)
            : await db.Set<AgentPolicyRun>().Where(x => x.Id == runId && x.DeviceId == deviceId).Select(x => x.ManagedWinGetActionsJson).SingleOrDefaultAsync(ct);
        return json is not null && JsonSerializer.Deserialize<List<CompiledPolicyActionDto>>(json)!.Any(x => Number(x.Settings, "ManagedCredentialId") == credentialId);
    }
    private static int Number(JsonElement settings, string name) => settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
