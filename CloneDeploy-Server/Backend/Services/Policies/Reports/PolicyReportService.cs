using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyReportDevice(int Id, string Name, bool Targeted, string Status,
    DateTime? StartedAtUtc, DateTime? FinishedAtUtc, DateTime? LastCheckinAtUtc, string? Detail);
public sealed record PolicyReportRow(int Id, string Name, string State, string? Hash, bool ReportingDisabled,
    int Agents, int Succeeded, int Failed, int Running, int NoResult, int Skipped, int NotApplicable);
public sealed record PolicyReportRun(string Device, string? User, string Hash, string Status,
    DateTime StartedAtUtc, DateTime? FinishedAtUtc, string? Detail);

public sealed class PolicyReportService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<PolicyReportService> logger)
{
    private async Task<bool> AllowedAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true &&
            (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportPolicies)).Succeeded;
    }

    public Task<ServiceResult<List<PolicyReportRow>>> ListAsync(bool includeInactive = false) => ServiceOperations.RunAsync(async () =>
    {
        if (!await AllowedAsync()) return ServiceResult<List<PolicyReportRow>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var policies = await db.Policies.AsNoTracking().Where(p => includeInactive ||
            (!p.Archived && p.ArchivedAtUtc == null && db.ActiveCompiledPolicies.Any(a => a.PolicyId == p.Id)))
            .OrderBy(p => p.Name).ToListAsync();
        var snapshots = await db.ActiveCompiledPolicies.AsNoTracking().ToDictionaryAsync(x => x.PolicyId, x => x.Json);
        var rows = new List<PolicyReportRow>();
        foreach (var policy in policies)
        {
            var active = !policy.Archived && policy.ArchivedAtUtc == null && snapshots.ContainsKey(policy.Id);
            var snapshot = active ? Parse(snapshots[policy.Id]) : null;
            var devices = snapshot is null ? [] : await DevicesCoreAsync(db, policy.Id, snapshot, false);
            rows.Add(new(policy.Id, policy.Name, active ? "Active" : policy.Archived || policy.ArchivedAtUtc != null ? "Archived" : "Inactive",
                snapshot?.Hash, snapshot is not null && Disabled(snapshot), devices.Count,
                devices.Count(x => x.Status == "Succeeded"), devices.Count(x => x.Status == "Failed"),
                devices.Count(x => x.Status == "Running"), devices.Count(x => x.Status == "No result"),
                devices.Count(x => x.Status == "Skipped"), devices.Count(x => x.Status == "Not applicable")));
        }
        return ServiceResult<List<PolicyReportRow>>.Ok(rows);
    }, logger);

    public Task<ServiceResult<List<PolicyReportDevice>>> DevicesAsync(int policyId) => ServiceOperations.RunAsync(async () =>
    {
        if (!await AllowedAsync()) return ServiceResult<List<PolicyReportDevice>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var json = await db.ActiveCompiledPolicies.AsNoTracking().Where(x => x.PolicyId == policyId && !x.Policy!.Archived && x.Policy.ArchivedAtUtc == null)
            .Select(x => x.Json).SingleOrDefaultAsync();
        return ServiceResult<List<PolicyReportDevice>>.Ok(json is null ? [] : await DevicesCoreAsync(db, policyId, Parse(json), true));
    }, logger);

    public Task<ServiceResult<List<PolicyReportRun>>> HistoryAsync(int policyId) => ServiceOperations.RunAsync(async () =>
    {
        if (!await AllowedAsync()) return ServiceResult<List<PolicyReportRun>>.Fail("Policy.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var runs = await (from run in db.Set<AgentPolicyRun>().AsNoTracking()
            join device in db.Devices on run.DeviceId equals device.Id
            where run.PolicyId == policyId
            orderby run.StartedAtUtc descending, run.Id descending
            select new { Run = run, device.Name }).Take(200).ToListAsync();
        return ServiceResult<List<PolicyReportRun>>.Ok(runs.Select(x =>
        {
            var disabled = Disabled(Parse(x.Run.SnapshotJson));
            return new PolicyReportRun(x.Name, x.Run.UserName, x.Run.Hash,
                disabled ? "Reporting disabled" : Status(x.Run.Result, x.Run.FinishedAtUtc),
                x.Run.StartedAtUtc, x.Run.FinishedAtUtc, disabled ? null : x.Run.Detail);
        }).ToList());
    }, logger);

    private static CompiledPolicySnapshotDto Parse(string json) => JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(json)
        ?? throw new InvalidDataException("Invalid policy snapshot.");
    private static bool Disabled(CompiledPolicySnapshotDto snapshot) =>
        snapshot.Policy.Settings.TryGetProperty("SkipServerResult", out var value) && value.GetBoolean();
    private static string Status(int? result, DateTime? finished) => finished is null ? "Running" : result switch
    {
        (int)PolicyRunResult.Success => "Succeeded", (int)PolicyRunResult.Failed => "Failed",
        (int)PolicyRunResult.Skipped => "Skipped", (int)PolicyRunResult.NotApplicable => "Not applicable", _ => "Unknown"
    };

    private static async Task<List<PolicyReportDevice>> DevicesCoreAsync(CloneDeployDbContext db, int policyId,
        CompiledPolicySnapshotDto snapshot, bool includeDetails)
    {
        var targets = await TargetIdsAsync(db, policyId);
        // Select the newest run per device in SQL; never load the full run history to count agents.
        var runs = await db.Set<AgentPolicyRun>().AsNoTracking().Where(x => x.PolicyId == policyId && x.Hash == snapshot.Hash)
            .GroupBy(x => x.DeviceId).Select(g => g.OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id)
                .Select(x => new { x.DeviceId, x.Result, x.StartedAtUtc, x.FinishedAtUtc, Detail = includeDetails ? x.Detail : null }).First())
            .ToListAsync();
        var byDevice = runs.ToDictionary(x => x.DeviceId);
        var ids = targets.Union(byDevice.Keys).ToArray();
        var devices = await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id) && d.ArchivedAtUtc == null &&
            d.ProvisionStatus == DeviceProvisionStatus.Provisioned).OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Name, d.LastCheckinAtUtc }).ToListAsync();
        return devices.Select(d =>
        {
            var run = byDevice.GetValueOrDefault(d.Id);
            return new PolicyReportDevice(d.Id, d.Name, targets.Contains(d.Id), Disabled(snapshot) ? "Reporting disabled" :
                run is null ? "No result" : Status(run.Result, run.FinishedAtUtc), run?.StartedAtUtc, run?.FinishedAtUtc,
                d.LastCheckinAtUtc, Disabled(snapshot) ? null : run?.Detail);
        }).ToList();
    }

    private static async Task<HashSet<int>> TargetIdsAsync(CloneDeployDbContext db, int policyId)
    {
        var groups = await db.GroupPolicies.Where(x => x.PolicyId == policyId && !x.DeviceGroup!.IsHidden)
            .Select(x => x.DeviceGroupId).ToArrayAsync();
        var ids = (await DeviceGroupMembershipQuery.DeviceIds(db, groups)
            .Union(db.DevicePolicies.Where(x => x.PolicyId == policyId).Select(x => x.DeviceId)).ToListAsync()).ToHashSet();
        var links = await db.DirectoryOuPolicies.AsNoTracking().Where(x => x.PolicyId == policyId &&
            x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly && x.DirectorySource.Provider == DirectoryProvider.ActiveDirectory).ToListAsync();
        foreach (var source in links.GroupBy(x => x.DirectorySourceId))
        {
            var nodes = await db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == source.Key && !x.IsMissing &&
                (x.Kind == DirectoryObjectKind.OrganizationalUnit || x.Kind == DirectoryObjectKind.Container || x.Kind == DirectoryObjectKind.Domain))
                .Select(x => new { x.ObjectGuid, x.ParentGuid }).ToListAsync();
            var existing = nodes.Select(x => x.ObjectGuid).ToHashSet();
            var parents = new HashSet<Guid>();
            foreach (var link in source.Where(x => existing.Contains(x.OuGuid)))
            {
                parents.Add(link.OuGuid);
                if (!link.IncludeDescendants) continue;
                var pending = new Queue<Guid>(); pending.Enqueue(link.OuGuid);
                var seen = new HashSet<Guid> { link.OuGuid };
                while (pending.TryDequeue(out var parent))
                    foreach (var child in nodes.Where(x => x.ParentGuid == parent))
                        if (seen.Add(child.ObjectGuid)) { parents.Add(child.ObjectGuid); pending.Enqueue(child.ObjectGuid); }
            }
            ids.UnionWith(await db.Devices.Where(d => d.DirectorySourceId == source.Key && db.DirectoryObjects.Any(o =>
                o.DirectorySourceId == source.Key && !o.IsMissing && o.Kind == DirectoryObjectKind.Computer &&
                o.ObjectGuid == d.DirectoryObjectGuid && o.ParentGuid.HasValue && parents.Contains(o.ParentGuid.Value)))
                .Select(d => d.Id).ToListAsync());
        }
        return ids;
    }
}
