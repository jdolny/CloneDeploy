using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public enum ImagingItemKind { Image, DeploymentProfile, DriverPack, ImagePrepProfile }

public sealed record ImagingRelation(string Kind, string Name, string? Href, string Details);
public sealed record ImagingHistoryEntry(DateTime AtUtc, string Name, string? Href, string Status, string Details);

public sealed class ImagingInspectionService(
    ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    private async Task<bool> CanViewAsync(ImagingItemKind kind)
    {
        var permission = kind switch
        {
            ImagingItemKind.Image => Constants.Permissions.ImageView,
            ImagingItemKind.DeploymentProfile => Constants.Permissions.DeploymentProfileView,
            ImagingItemKind.DriverPack => Constants.Permissions.DriverPackView,
            ImagingItemKind.ImagePrepProfile => Constants.Permissions.ImagePrepProfileView,
            _ => Constants.Permissions.AdminOnly
        };
        return await ImagingPermissions.BrowserAsync(authentication, authorization, permission) is not null;
    }

    public async Task<ServiceResult<List<ImagingRelation>>> GetRelationsAsync(ImagingItemKind kind, int id)
    {
        if (!await CanViewAsync(kind)) return ServiceResult<List<ImagingRelation>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var rows = new List<ImagingRelation>();
        if (kind is ImagingItemKind.Image or ImagingItemKind.DeploymentProfile)
        {
            var devices = await db.Devices.AsNoTracking()
                .Where(x => kind == ImagingItemKind.Image ? x.ImageId == id : x.DeploymentProfileId == id)
                .Select(x => new { x.Id, x.Name }).ToListAsync();
            rows.AddRange(devices.Select(x => new ImagingRelation("Devices", x.Name, $"/devices/manage/{x.Id}", "Assigned imaging default")));
            var groups = await db.DeviceGroups.AsNoTracking()
                .Where(x => kind == ImagingItemKind.Image ? x.ImageId == id : x.DeploymentProfileId == id)
                .Select(x => new { x.Id, x.Name }).ToListAsync();
            rows.AddRange(groups.Select(x => new ImagingRelation("Device groups", x.Name, $"/groups/manage/{x.Id}", "Assigned imaging default")));
        }
        if (kind == ImagingItemKind.Image)
        {
            var tags = await db.Set<ImageTag>().AsNoTracking().Where(x => x.ImageId == id).Select(x => x.Tag!.Name).ToListAsync();
            rows.AddRange(tags.Select(x => new ImagingRelation("Tags", x, null, "Image tag")));
        }
        if (kind == ImagingItemKind.DeploymentProfile)
        {
            var profile = await db.ImageProfiles.AsNoTracking().Include(x => x.DriverPack).SingleOrDefaultAsync(x => x.Id == id);
            if (profile?.DriverPack is { } pack)
                rows.Add(new("Driver packs", pack.Name, $"/images/driverpacks/{pack.Id}", "Selected driver pack"));
            var rules = await db.Set<DeploymentDriverRule>().AsNoTracking().Include(x => x.DriverPack).Where(x => x.DeploymentProfileId == id).ToListAsync();
            rows.AddRange(rules.Select(x => new ImagingRelation("Driver packs", x.DriverPack!.Name, $"/images/driverpacks/{x.DriverPackId}", "Driver matching rule")));
            var policies = await db.Set<DeploymentPolicy>().AsNoTracking().Include(x => x.Policy).Where(x => x.DeploymentProfileId == id).OrderBy(x => x.Position).ToListAsync();
            rows.AddRange(policies.Select(x => new ImagingRelation("Policies", x.Policy!.Name, $"/policies/{x.PolicyId}", $"Post-deployment policy, position {x.Position + 1}")));
        }
        if (kind == ImagingItemKind.DriverPack)
        {
            var profiles = db.ImageProfiles.AsNoTracking().Where(x => x.DriverPackId == id || db.Set<DeploymentDriverRule>().Any(r => r.DeploymentProfileId == x.Id && r.DriverPackId == id));
            rows.AddRange((await profiles.Select(x => new { x.Id, x.Name }).ToListAsync())
                .Select(x => new ImagingRelation("Deployment profiles", x.Name, $"/images/deploymentprofiles/{x.Id}", "Referenced by deployment profile")));
        }
        if (kind == ImagingItemKind.DriverPack)
        {
            var pack = await db.DriverProfiles.AsNoTracking().Include(x => x.Files).ThenInclude(x => x.File)
                .Include(x => x.Tags).ThenInclude(x => x.Tag).Include(x => x.UpdateSchedule).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id);
            if (pack is not null)
            {
                rows.AddRange(pack.Files.Select(x => new ImagingRelation("Files", x.File.Name, $"/images/driverpacks/{id}/files", "Driver source file")));
                rows.AddRange(pack.Tags.Select(x => new ImagingRelation("Tags", x.Tag!.Name, null, "Driver pack tag")));
                if (pack.UpdateSchedule is { } schedule)
                    rows.Add(new("Schedules", schedule.Name, $"/images/driverpacks/{id}/configuration", "Driver update schedule"));
            }
        }
        // Prep profiles are selected at run time; they have no persistent device assignments.
        return ServiceResult<List<ImagingRelation>>.Ok(rows.Distinct().OrderBy(x => x.Kind).ThenBy(x => x.Name).ToList());
    }

    public async Task<ServiceResult<List<AuditLog>>> GetAuditAsync(ImagingItemKind kind, int id)
    {
        if (!await CanViewAsync(kind)) return ServiceResult<List<AuditLog>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var type = kind.ToString();
        return ServiceResult<List<AuditLog>>.Ok(await db.Set<AuditLog>().AsNoTracking()
            .Where(x => x.ObjectType == type && x.ObjectId == id)
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync());
    }

    public async Task<ServiceResult<List<ImagingHistoryEntry>>> GetHistoryAsync(ImagingItemKind kind, int id)
    {
        if (!await CanViewAsync(kind)) return ServiceResult<List<ImagingHistoryEntry>>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (kind is ImagingItemKind.Image or ImagingItemKind.DeploymentProfile)
        {
            var deployments = await (from run in db.Set<ImagingDeployment>().AsNoTracking()
                join device in db.Devices on run.DeviceId equals device.Id
                join version in db.ImageVersions on run.ImageVersionId equals version.Id
                where kind == ImagingItemKind.Image ? version.ImageId == id : run.DeploymentProfileId == id
                orderby run.CreatedAtUtc descending, run.Id
                select new { Run = run, DeviceName = device.Name, VersionName = version.Name, version.Number })
                .Take(200).ToListAsync();
            return ServiceResult<List<ImagingHistoryEntry>>.Ok(deployments.Select(x => new ImagingHistoryEntry(
                x.Run.CreatedAtUtc, x.DeviceName, $"/devices/manage/{x.Run.DeviceId}",
                x.Run.CancelledAtUtc != null ? "Cancelled" : x.Run.SetupCompletedAtUtc != null ? "Completed" :
                x.Run.ImagerFailedAtUtc != null ? "Failed" : x.Run.ImagingCompletedAtUtc != null ? "Awaiting setup completion" :
                x.Run.ImagerStartedAtUtc != null ? "Imaging" : "Pending",
                $"Version {x.Number}: {x.VersionName}" )).ToList());
        }
        if (kind == ImagingItemKind.ImagePrepProfile)
        {
            // Filter before limiting so frequent edits cannot crowd authorization records out of history.
            var logs = await db.Set<AuditLog>().AsNoTracking().Where(x => x.ObjectType == nameof(ImagePrepProfile) && x.ObjectId == id
                && x.ObjectJson.Contains("Image prep authorized"))
                .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync();
            var parsed = logs.Select(x => (Log: x, Guid: AuthorizedDevice(x.ObjectJson))).Where(x => x.Guid.HasValue).ToList();
            var guids = parsed.Select(x => x.Guid!.Value).Distinct().ToArray();
            var devices = await db.Devices.AsNoTracking().Where(x => guids.Contains(x.Guid)).Select(x => new { x.Id, x.Guid, x.Name }).ToListAsync();
            return ServiceResult<List<ImagingHistoryEntry>>.Ok(parsed.Select(x =>
            {
                var device = devices.FirstOrDefault(d => d.Guid == x.Guid);
                return new ImagingHistoryEntry(x.Log.LoggedAtUtc, device?.Name ?? x.Guid.ToString()!,
                    device is null ? null : $"/devices/manage/{device.Id}", "Authorized", x.Log.UserName);
            }).ToList());
        }
        return ServiceResult<List<ImagingHistoryEntry>>.Ok([]);
    }

    private static Guid? AuthorizedDevice(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Action", out var action)
                && action.ValueKind == JsonValueKind.String && action.GetString() == "Image prep authorized"
                && root.TryGetProperty("DeviceGuid", out var device) && device.ValueKind == JsonValueKind.String
                && Guid.TryParse(device.GetString(), out var guid) ? guid : null;
        }
        catch (JsonException) { return null; }
    }
}
