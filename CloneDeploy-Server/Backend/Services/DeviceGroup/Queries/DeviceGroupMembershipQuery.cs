using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Resolves stored memberships and the live built-in group without materializing member rows.</summary>
internal static class DeviceGroupMembershipQuery
{
    internal static IQueryable<Device> AllDevices(CloneDeployDbContext db) =>
        db.Devices.Where(d => d.ArchivedAtUtc == null && d.ProvisionStatus == DeviceProvisionStatus.Provisioned);

    internal static IQueryable<int> DeviceIds(CloneDeployDbContext db, int[] groupIds) =>
        db.DeviceGroupMemberships.Where(m => groupIds.Contains(m.DeviceGroupId) && m.DeviceGroup!.Type != DeviceGroupType.AllDevices)
            .Select(m => m.DeviceId)
            .Union(AllDevices(db).Where(d => db.DeviceGroups.Any(g => groupIds.Contains(g.Id) && g.Type == DeviceGroupType.AllDevices))
                .Select(d => d.Id));

    internal static IQueryable<DeviceGroup> ForDevice(CloneDeployDbContext db, int deviceId)
    {
        var eligible = AllDevices(db);
        return db.DeviceGroups.Where(g => g.Type == DeviceGroupType.AllDevices
            ? eligible.Any(d => d.Id == deviceId)
            : db.DeviceGroupMemberships.Any(m => m.DeviceGroupId == g.Id && m.DeviceId == deviceId));
    }
}
