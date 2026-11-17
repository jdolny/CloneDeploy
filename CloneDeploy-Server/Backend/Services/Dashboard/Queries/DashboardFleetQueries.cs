using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

internal static class DashboardFleetQueries
{
    internal static readonly string[] UpdateBuckets = ["updates-missing", "updates-restart", "updates-stale"];
    internal static readonly string[] AgentBuckets = ["agent-current", "agent-older", "agent-newer", "agent-unknown"];
    private static IQueryable<Device> Managed(CloneDeployDbContext db) => db.Devices.AsNoTracking()
        .Where(d => d.ArchivedAtUtc == null && d.ProvisionStatus == DeviceProvisionStatus.Provisioned);

    internal static Version? VersionOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return AgentUpdateOffer.ParseVersion(value); }
        catch (InvalidDataException) { return null; }
    }
    private static string AgentBucket(string? value, Version target)
    {
        var version = VersionOf(value);
        return version is null ? "agent-unknown" : version < target ? "agent-older" : version > target ? "agent-newer" : "agent-current";
    }
    private static IQueryable<Device> Updates(IQueryable<Device> devices, string bucket, DateTime asOf)
    {
        var cutoff = asOf.AddDays(-7);
        return bucket switch
        {
            "updates-missing" => devices.Where(d => d.WindowsUpdates.Any(u => !u.IsInstalled && u.IsHidden != true &&
                u.RevisionNumber >= 0 && u.WindowsUpdate!.ApprovedRevisionNumber == u.RevisionNumber &&
                u.WindowsUpdate.RevisionNumber == u.RevisionNumber && u.WindowsUpdate.UpdateId != null)),
            "updates-restart" => devices.Where(d => d.WindowsUpdateScan != null && d.WindowsUpdateScan.RebootRequired == true),
            "updates-stale" => devices.Where(d => d.WindowsUpdateScan == null || d.WindowsUpdateScan.LastSuccessfulOnlineScanAtUtc == null ||
                d.WindowsUpdateScan.LastSuccessfulOnlineScanAtUtc < cutoff),
            _ => throw new ArgumentException("Invalid update bucket.")
        };
    }
    internal static async Task<DashboardFleetDto> ReadAsync(CloneDeployDbContext db, DateTime asOf, string? preparedVersion)
    {
        var devices = Managed(db);
        var updates = new List<DashboardFleetCount>();
        foreach (var bucket in UpdateBuckets) updates.Add(new(bucket, await Updates(devices, bucket, asOf).CountAsync()));
        var versions = await devices.GroupBy(d => d.ClientVersion).Select(g => new { Version = g.Key, Count = g.Count() }).ToListAsync();
        var target = VersionOf(preparedVersion);
        var agents = target is null ? [] : AgentBuckets.Select(bucket => new DashboardFleetCount(bucket,
            versions.Where(v => AgentBucket(v.Version, target) == bucket).Sum(v => v.Count))).ToList();
        return new(versions.Sum(v => v.Count), target?.ToString(), updates, agents);
    }
    internal static async Task<List<DashboardFleetDeviceDto>> DevicesAsync(CloneDeployDbContext db, string bucket, DateTime asOf, string? targetVersion)
    {
        var devices = Managed(db);
        if (UpdateBuckets.Contains(bucket)) devices = Updates(devices, bucket, asOf);
        else
        {
            var target = VersionOf(targetVersion)!;
            // Compare normalized MSI versions once per distinct reported version, then filter before the result limit.
            var reported = await devices.Select(d => d.ClientVersion).Distinct().ToListAsync();
            var matching = reported.Where(v => AgentBucket(v, target) == bucket).ToArray();
            devices = devices.Where(d => matching.Contains(d.ClientVersion));
        }
        return await devices.OrderBy(d => d.Name).ThenBy(d => d.Id).Take(200)
            .Select(d => new DashboardFleetDeviceDto(d.Id, d.Name, d.ClientVersion, d.LastCheckinAtUtc,
                d.WindowsUpdateScan == null ? null : d.WindowsUpdateScan.LastSuccessfulOnlineScanAtUtc,
                d.WindowsUpdateScan == null ? null : d.WindowsUpdateScan.RebootRequired)).ToListAsync();
    }
}
