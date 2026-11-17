using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Refreshes projections off the UDP thread; inventory events reload only changed devices.</summary>
public sealed class NativePxeReservationWorker(IServiceScopeFactory scopes, NativePxeChanges changes,
    NativePxeReservations reservations, NativePxePublication publication,
    ILogger<NativePxeReservationWorker> logger) : BackgroundService
{
    private sealed record DeviceMatch(int DefinitionId, string[] Macs)
    {
        public int Arm64DefinitionId { get; init; }
    }
    private readonly Dictionary<int, DeviceMatch> _devices = [];
    private readonly HashSet<string> _ambiguous = [];
    private List<WinPeDefinition> _definitions = [];
    private Dictionary<int, NativePxeTarget> _targets = [];
    private Dictionary<int, NativePxeTarget> _arm64Targets = [];
    private string? _rulesKey;
    private DateTime _reconcile;
    private int _conflicts;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = changes.Drain();
            if (changes.TakeReconciliation()) _reconcile = DateTime.MinValue;
            try { await RefreshAsync(pending.Rules, pending.Devices, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _reconcile = DateTime.MinValue; changes.RulesChanged(); reservations.ReportError(); logger.LogError(ex, "Native PXE reservation refresh failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
    public async Task RefreshAsync(bool rulesChanged, int[] changedDevices, CancellationToken ct = default)
    {
        var reconcile = DateTime.UtcNow >= _reconcile;
        if (!rulesChanged && changedDevices.Length == 0 && !reconcile) return;
        var reloadRules = rulesChanged || reconcile || _rulesKey is null;
        if (!reloadRules && !_definitions.Any(x => x.NativePxe)) return;
        using var scope = scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>();
        await using var db = await factory.CreateContextAsync();
        var definitions = _definitions;
        var targets = _targets;
        var arm64Targets = _arm64Targets;
        if (reloadRules)
        {
            // Project only selection fields; inventory notifications reuse cached rules and targets.
            definitions = await db.WinPeDefinitions.AsNoTracking().Where(x => !x.IsBase && x.PublishedBuildId != null)
                .Select(x => new WinPeDefinition { Id = x.Id, Name = x.Name, NativePxe = x.NativePxe,
                    IsDefault = x.IsDefault, IsArm64Default = x.IsArm64Default, ModelRulesJson = x.ModelRulesJson, PublishedBuildId = x.PublishedBuildId })
                .OrderBy(x => x.Id).ToListAsync(ct);
            targets = [];
            arm64Targets = [];
            foreach (var definition in definitions.Where(x => x.NativePxe))
            {
                if (await publication.IsReadyAsync(definition.PublishedBuildId!.Value, ct))
                    targets[definition.Id] = new(definition.Id, definition.PublishedBuildId.Value);
                if (await publication.IsArm64ReadyAsync(definition.PublishedBuildId!.Value, ct))
                    arm64Targets[definition.Id] = new(definition.Id, definition.PublishedBuildId.Value) { Architecture = "arm64" };
            }
        }
        var key = JsonSerializer.Serialize(definitions.Select(x => new { x.Id, x.NativePxe, x.IsDefault, x.IsArm64Default, x.ModelRulesJson, Arm64Ready = arm64Targets.ContainsKey(x.Id) }));
        var full = key != _rulesKey || reconcile;
        if (!definitions.Any(x => x.NativePxe)) { _devices.Clear(); _ambiguous.Clear(); _conflicts = 0; }
        else
        {
            var query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null);
            if (!full) query = query.Where(x => changedDevices.Contains(x.Id));
            if (full || changedDevices.Length != 0)
            {
                var models = await query.Select(x => new { DeviceId = x.Id,
                    Manufacturer = x.ComputerSystem == null ? null : x.ComputerSystem.Manufacturer,
                    Model = x.ComputerSystem == null ? null : x.ComputerSystem.Model }).ToListAsync(ct);
                var selected = new Dictionary<int, int>();
                var selectedArm64 = new Dictionary<int, int>();
                var arm64Definitions = definitions.Where(x => arm64Targets.ContainsKey(x.Id)).Select(x => new WinPeDefinition
                {
                    Id = x.Id, Name = x.Name, PublishedBuildId = x.PublishedBuildId, NativePxe = x.NativePxe,
                    ModelRulesJson = x.ModelRulesJson, IsDefault = x.IsArm64Default
                }).ToArray();
                _conflicts = 0;
                foreach (var group in models.GroupBy(x => (x.Manufacturer, x.Model)))
                {
                    try
                    {
                        var armMatch = WinPeModelMatching.ResolveForAgent(arm64Definitions, group.Key.Manufacturer, group.Key.Model);
                        foreach (var device in group) selectedArm64[device.DeviceId] = armMatch.Id;
                    }
                    catch (InvalidOperationException) { /* No compatible unique ARM64 native selection. */ }
                    WinPeDefinition match;
                    try { match = WinPeModelMatching.ResolveForAgent(definitions, group.Key.Manufacturer, group.Key.Model); }
                    catch (InvalidOperationException ex)
                    {
                        // Conflicting rules or no ready default retain normal iPXE.
                        _conflicts += group.Count();
                        logger.LogDebug(ex, "No unique native PXE model selection for {Manufacturer} {Model}", group.Key.Manufacturer, group.Key.Model);
                        continue;
                    }
                    if (match.NativePxe) foreach (var device in group) selected[device.DeviceId] = match.Id;
                }
                var macs = new Dictionary<int, List<string>>();
                var affectedMacs = (full ? _devices.Values : _devices.Where(x => changedDevices.Contains(x.Key)).Select(x => x.Value))
                    .SelectMany(x => x.Macs).ToHashSet();
                // Track unmatched devices too so a native default cannot override their
                // iPXE selection. Inventory updates still query only changed devices.
                foreach (var ids in (full ? models.Select(x => x.DeviceId) : changedDevices.AsEnumerable()).Distinct().Chunk(1000))
                {
                    var rows = await db.Set<DeviceNic>().AsNoTracking().Where(x => ids.Contains(x.DeviceId))
                        .Select(x => new { x.DeviceId, x.Mac }).ToListAsync(ct);
                    foreach (var row in rows)
                    {
                        var mac = NativePxeReservations.Normalize(row.Mac);
                        if (mac is null) continue;
                        affectedMacs.Add(mac);
                        if (!macs.TryGetValue(row.DeviceId, out var list)) macs[row.DeviceId] = list = [];
                        list.Add(mac);
                    }
                }
                // Apply only after every query succeeded, preserving the old snapshot on failure.
                if (full) { _devices.Clear(); _ambiguous.Clear(); }
                else foreach (var id in changedDevices) _devices.Remove(id);

                foreach (var id in models.Select(x => x.DeviceId))
                    _devices[id] = new(selected.GetValueOrDefault(id), macs.GetValueOrDefault(id)?.Distinct().ToArray() ?? [])
                        { Arm64DefinitionId = selectedArm64.GetValueOrDefault(id) };
            }
        }
        _ambiguous.Clear();
        var reasons = new Dictionary<string, string>();
        var defaultDefinition = definitions.SingleOrDefault(x => x.IsDefault);
        var arm64DefaultDefinition = definitions.SingleOrDefault(x => x.IsArm64Default);
        var defaultTarget = defaultDefinition is null ? null : targets.GetValueOrDefault(defaultDefinition.Id);
        var arm64Default = arm64DefaultDefinition is null ? null : arm64Targets.GetValueOrDefault(arm64DefaultDefinition.Id);
        var resolved = new List<(int DeviceId, string Mac, NativePxeTarget Target)>();
        foreach (var group in _devices.SelectMany(x => x.Value.Macs.Select(mac => (Id: x.Key, Mac: mac, Match: x.Value))).GroupBy(x => x.Mac))
        {
            foreach (var arm64 in new[] { false, true })
            {
                var ready = arm64 ? arm64Targets : targets;
                var choices = group.Select(x => ready.GetValueOrDefault(arm64 ? x.Match.Arm64DefinitionId : x.Match.DefinitionId)).ToArray();
                var nativeCount = choices.Where(x => x is not null).Distinct().Count();
                var duplicate = group.Count() > 1;
                var target = NativePxeReservations.ResolveSelection(choices, arm64 ? arm64Default : defaultTarget, duplicate);
                var decisionKey = group.Key + (arm64 ? "|arm64" : "");
                var owners = string.Join(", ", group.Select(x => x.Id));
                var reason = target is not null
                    ? nativeCount == 1 ? $"Using the single ready native selection: environment {target.DefinitionId}, build {target.BuildId}."
                        : $"Using the ready native default because duplicate records have {(nativeCount == 0 ? "no native selection" : "conflicting native selections")}: environment {target.DefinitionId}, build {target.BuildId}."
                    : nativeCount > 1 ? "Conflicting native selections without a ready native default; using iPXE."
                        : "No ready native selection or applicable native default; using iPXE.";
                reasons[decisionKey] = $"Device records {owners}. {reason}";
                if (target is not null) resolved.Add((group.First().Id, group.Key, target));
                else if (nativeCount > 1) _ambiguous.Add(decisionKey);
                if (duplicate)
                    logger.LogDebug("Duplicate MAC {Mac}, architecture {Architecture}: {Reason}", group.Key, arm64 ? "arm64" : "x64/BIOS", reasons[decisionKey]);
            }
        }
        var missing = definitions.Count(x => x.NativePxe) - targets.Count;
        reservations.ReplaceWithDefaults(resolved,
            $"Native PXE: {targets.Count} ready boot environments. {missing} unstaged boot environments; {_ambiguous.Count} conflicting MAC selections without a native default; {_conflicts} devices without a unique model selection in the last refresh.",
            defaultTarget, arm64Default, _devices.Values.SelectMany(x => x.Macs));
        reservations.SetDecisionReasons(reasons);
        _definitions = definitions;
        _targets = targets;
        _arm64Targets = arm64Targets;
        _rulesKey = key;
        if (reconcile) _reconcile = DateTime.UtcNow.AddMinutes(10);
        var transfers = scope.ServiceProvider.GetService<TftpWorker>()?.ActiveTransfers ?? 0;
        if (reloadRules) await publication.CleanupAsync(definitions.Where(x => x.NativePxe).Select(x => x.PublishedBuildId!.Value).ToHashSet(), transfers != 0, ct);
    }
}
