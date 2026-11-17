using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceStatsVolume(string Drive, int Size, int Free, int Percent)
{
    public bool Valid => Size > 0 && Free >= 0 && Free <= Size && Percent is >= 0 and <= 100;
    public bool Low => Valid && (Free <= 10 || Percent <= 10);
    public bool Critical => Valid && (Free <= 5 || Percent <= 5);
}
public sealed class DeviceStatsRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateTime? Checkin { get; init; }
    public DateTime? Inventory { get; init; }
    public string? Os { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Agent { get; init; }
    public int MemoryMb { get; init; }
    public List<DeviceStatsVolume> Volumes { get; init; } = [];
    public List<string?> DiskStatuses { get; init; } = [];
    public bool LowDisk => Volumes.Any(x => x.Low);
    public bool DiskWarning => DiskStatuses.Any(IsDiskWarning);
    public string DiskNotes => string.Join("; ", Volumes.Where(x => x.Low).Select(x => $"{x.Drive}: {x.Free} GiB free ({x.Percent}%)")
        .Concat(DiskStatuses.Where(IsDiskWarning).Select(x => $"Disk status: {x}")));
    private static bool IsDiskWarning(string? value) => value?.Trim().ToLowerInvariant() is
        "pred fail" or "degraded" or "error" or "nonrecover" or "stressed" or "failed" or "failing";
}
public sealed record DeviceStatsBucket(string Label, List<DeviceStatsRow> Devices);
public sealed record DeviceStatsChart(string Title, string Description, List<DeviceStatsBucket> Buckets);
public sealed record DeviceStatsSnapshot(DateTime AsOfUtc, List<DeviceStatsRow> Devices, List<DeviceStatsChart> Charts, long CapacityGiB, long FreeGiB);

public sealed class DeviceStatsService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<DeviceStatsService> logger)
{
    public Task<ServiceResult<DeviceStatsSnapshot>> GetAsync() => ServiceOperations.RunAsync(async () =>
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true || !(await authorization.AuthorizeAsync(user, Constants.Permissions.ReportDevices)).Succeeded)
            return ServiceResult<DeviceStatsSnapshot>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var now = DateTime.UtcNow;
        var devices = await db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new DeviceStatsRow
            {
                Id = x.Id, Name = x.Name, Checkin = x.LastCheckinAtUtc, Inventory = x.LastInventoryAtUtc, Agent = x.ClientVersion,
                Os = x.Os == null ? null : x.Os.Caption,
                Manufacturer = x.ComputerSystem == null ? null : x.ComputerSystem.Manufacturer,
                Model = x.ComputerSystem == null ? null : x.ComputerSystem.Model,
                MemoryMb = x.ComputerSystem == null ? 0 : x.ComputerSystem.Memory,
                Volumes = x.LogicalVolumes.Select(v => new DeviceStatsVolume(v.Drive, v.SizeGB, v.FreeSpaceGB, v.FreeSpacePercent)).ToList(),
                DiskStatuses = x.HardDrives.Select(d => d.Status).ToList()
            }).AsSplitQuery().ToListAsync();
        return ServiceResult<DeviceStatsSnapshot>.Ok(Summarize(devices, now));
    }, logger, "Device.Failed");

    public static DeviceStatsSnapshot Summarize(List<DeviceStatsRow> devices, DateTime now)
    {
        List<DeviceStatsBucket> Group(Func<DeviceStatsRow, string> key, bool limit = false)
        {
            var groups = devices.GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DeviceStatsBucket(g.Key, g.ToList())).OrderByDescending(g => g.Devices.Count).ThenBy(g => g.Label).ToList();
            if (!limit || groups.Count <= 8) return groups;
            return [.. groups.Take(8), new("Other", groups.Skip(8).SelectMany(g => g.Devices).OrderBy(x => x.Name).ToList())];
        }
        var volumes = devices.SelectMany(x => x.Volumes).Where(x => x.Valid).ToList();
        return new(now, devices,
        [
            new("Check-in activity", "Last recorded check-in; this is not live online status.", Group(x => x.Checkin is not { } last ? "Never checked in" : last >= now.AddDays(-1) ? "Within 24 hours" : last >= now.AddDays(-7) ? "1–7 days ago" : last >= now.AddDays(-30) ? "7–30 days ago" : "Over 30 days ago")),
            new("Disk space", "Worst reported fixed volume per device. Critical: ≤5 GiB or ≤5% free. Low: ≤10 GiB or ≤10% free.", Group(x => x.Volumes.Any(v => v.Critical) ? "Critical space" : x.LowDisk ? "Low space" : x.Volumes.Any(v => v.Valid) ? "Above thresholds" : "No valid volume data")),
            new("Reported disk status", "Windows disk status, not detailed SMART telemetry. Missing or unrecognized status is not assumed healthy.", Group(x => x.DiskWarning ? "Warning reported" : x.DiskStatuses.Count > 0 && x.DiskStatuses.All(s => string.Equals(s?.Trim(), "OK", StringComparison.OrdinalIgnoreCase)) ? "All disks report OK" : "Unknown / other status")),
            new("Inventory freshness", "Age of the last inventory received.", Group(x => x.Inventory is not { } last ? "No inventory timestamp" : last >= now.AddDays(-7) ? "Within 7 days" : last >= now.AddDays(-30) ? "7–30 days ago" : "Over 30 days ago")),
            new("Operating systems", "Most common OS names. Smaller groups are combined into Other.", Group(x => Label(x.Os), true)),
            new("Manufacturers", "Most common manufacturers from inventory.", Group(x => Label(x.Manufacturer), true)),
            new("Models", "Most common hardware models from inventory.", Group(x => Label(x.Model), true)),
            new("Agent versions", "Reported versions; this does not determine whether an agent is current.", Group(x => Label(x.Agent), true)),
            new("Installed memory", "Memory reported by inventory, grouped by capacity.", Group(x => x.MemoryMb <= 0 ? "Unknown" : x.MemoryMb < 8 * 1024 ? "Under 8 GiB" : x.MemoryMb < 16 * 1024 ? "8–under 16 GiB" : x.MemoryMb < 32 * 1024 ? "16–under 32 GiB" : "32 GiB or more"))
        ], volumes.Sum(x => (long)x.Size), volumes.Sum(x => (long)x.Free));
    }
    private static string Label(string? text) => string.IsNullOrWhiteSpace(text) ? "Unknown / no inventory" : text.Trim();
}
