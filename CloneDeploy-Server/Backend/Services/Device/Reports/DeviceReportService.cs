using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class DeviceReportService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, ILogger<DeviceReportService> logger)
{
    public Task<ServiceResult<List<DeviceReportRow>>> GetAsync() => ServiceOperations.RunAsync(async () =>
    {
        if ((await authentication.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated != true)
            return ServiceResult<List<DeviceReportRow>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var eligible = DeviceGroupMembershipQuery.AllDevices(db).Select(d => d.Id);
        var rows = await db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null)
            .OrderBy(x => x.Name).Select(x => new DeviceReportRow
            {
                Id = x.Id, Name = x.Name, LastCheckin = x.LastCheckinAtUtc,
                Os = x.Os == null ? null : x.Os.Caption,
                Version = x.Os == null ? null : x.Os.Version,
                Build = x.Os == null ? null : x.Os.BuildNumber,
                DirectoryPath = x.DirectoryObject == null ? null : x.DirectoryObject.DistinguishedName,
                Groups = db.DeviceGroups.Where(g => g.Type == CloneDeploy_Server.Backend.Enum.DeviceGroupType.AllDevices
                    ? eligible.Contains(x.Id)
                    : db.DeviceGroupMemberships.Any(m => m.DeviceGroupId == g.Id && m.DeviceId == x.Id))
                    .Select(g => g.Name).ToList(),
                Tags = x.Tags.Select(t => t.Tag!.Name).ToList()
            }).ToListAsync();
        return ServiceResult<List<DeviceReportRow>>.Ok(rows);
    }, logger, "Device.Failed");
}

public sealed class DeviceReportRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateTime? LastCheckin { get; init; }
    public string? Os { get; init; }
    public string? Version { get; init; }
    public string? Build { get; init; }
    public string? DirectoryPath { get; init; }
    public List<string> Groups { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string OsLabel => string.IsNullOrWhiteSpace(Os) ? "Unknown / no inventory" : Os;
    public string Activity(DateTime now) => LastCheckin is not { } last ? "Never checked in" :
        last >= now.AddDays(-7) ? "Within 7 days" : last >= now.AddDays(-30) ? "7–30 days ago" :
        last >= now.AddDays(-90) ? "30–90 days ago" : "Over 90 days ago";
}
