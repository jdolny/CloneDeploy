using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record EffectiveImagingSettings(int? ImageId, int? ProfileId, string Source, string? Error = null);

internal static class ImagingInheritance
{
    internal static async Task<EffectiveImagingSettings> ResolveAsync(CloneDeployDbContext db, Device device)
    {
        if (device.ImageId.HasValue || device.DeploymentProfileId.HasValue)
            return new(device.ImageId, device.DeploymentProfileId, "Device assignment",
                device.ImageId.HasValue && device.DeploymentProfileId.HasValue ? null : "Assign both an image and a deployment profile, or clear both to inherit.");
        var groups = await DeviceGroupMembershipQuery.ForDevice(db, device.Id)
            .Where(x => x.ImageId != null || x.DeploymentProfileId != null).OrderBy(x => x.Name).ToListAsync();
        if (groups.Count == 0) return new(null, null, "Not assigned");
        var source = "Group: " + string.Join(", ", groups.Select(x => x.Name));
        if (groups.Any(x => x.ImageId == null || x.DeploymentProfileId == null))
            return new(null, null, source, "A group imaging assignment is incomplete. Assign both an image and a profile, or clear both on the group.");
        if (groups.Select(x => (x.ImageId, x.DeploymentProfileId)).Distinct().Count() != 1)
            return new(null, null, source, "Conflicting group imaging assignments. Set a device override or make the group assignments agree before deploying.");
        return new(groups[0].ImageId, groups[0].DeploymentProfileId, source);
    }
}
