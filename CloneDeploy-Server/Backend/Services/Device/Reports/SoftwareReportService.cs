using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record SoftwareReportRow(int Id, string? Name, string? Version, int DeviceCount);
public sealed record SoftwareReportDevice(int Id, string Name, bool Archived, DateTime? LastInventoryAtUtc)
{
    public bool CanUninstall { get; init; }
}

public sealed class SoftwareReportService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<SoftwareReportService> logger)
{
    private async Task<bool> CanViewAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportSoftware)).Succeeded;
    }

    public Task<ServiceResult<List<SoftwareReportRow>>> ListAsync() => ServiceOperations.RunAsync(async () =>
    {
        if (!await CanViewAsync()) return ServiceResult<List<SoftwareReportRow>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var rows = await db.Software.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Version).ThenBy(x => x.Id)
            .Select(x => new SoftwareReportRow(x.Id, x.Name, x.Version,
                db.DeviceSoftware.Where(d => d.SoftwareId == x.Id).Select(d => d.DeviceId).Distinct().Count())).ToListAsync();
        return ServiceResult<List<SoftwareReportRow>>.Ok(rows);
    }, logger, "Device.Failed");

    public Task<ServiceResult<List<SoftwareReportDevice>>> DevicesAsync(int softwareId) => ServiceOperations.RunAsync(async () =>
    {
        if (!await CanViewAsync()) return ServiceResult<List<SoftwareReportDevice>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var rows = await db.Devices.AsNoTracking().Where(x => db.DeviceSoftware.Any(d => d.DeviceId == x.Id && d.SoftwareId == softwareId))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new SoftwareReportDevice(x.Id, x.Name,
                x.ArchivedAtUtc != null || x.ProvisionStatus == DeviceProvisionStatus.Archived, x.LastInventoryAtUtc)
                {
                    CanUninstall = x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived &&
                        db.DeviceSoftware.Any(d => d.DeviceId == x.Id && d.SoftwareId == softwareId &&
                            d.UninstallString != null && d.UninstallString.Trim() != "")
                }).ToListAsync();
        return ServiceResult<List<SoftwareReportDevice>>.Ok(rows);
    }, logger, "Device.Failed");
}
