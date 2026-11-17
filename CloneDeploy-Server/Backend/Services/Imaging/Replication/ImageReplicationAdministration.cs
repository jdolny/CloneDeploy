using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ImageReplicationSettings(List<StorageProvider> Providers, List<int> Destinations, List<ImageReplica> Copies, Dictionary<Guid, string> Versions);

public sealed class ImageReplicationAdministration(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public async Task<ServiceResult<ImageReplicationSettings>> GetAsync(int imageId)
    {
        if (await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageView) is null) return ServiceResult<ImageReplicationSettings>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await db.Images.AnyAsync(x => x.Id == imageId)) return ServiceResult<ImageReplicationSettings>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var targets = await db.Set<ImageReplicationTarget>().Where(x => x.ImageId == imageId).Select(x => x.StorageProviderId).ToListAsync();
        var providers = await db.StorageProviders.AsNoTracking().Where(x => targets.Contains(x.Id) || db.Set<ImageReplica>().Any(r => r.ImageId == imageId && r.StorageProviderId == x.Id) ||
            x.IsEnabled && (x.Purposes & StoragePurpose.Imaging) != 0).OrderBy(x => x.Name).Select(x => new StorageProvider { Id = x.Id, Name = x.Name, IsEnabled = x.IsEnabled }).ToListAsync();
        var copies = await db.Set<ImageReplica>().AsNoTracking().Where(x => x.ImageId == imageId).OrderByDescending(x => x.UpdatedAtUtc).ToListAsync();
        var versions = await db.ImageVersions.Where(x => x.ImageId == imageId).ToDictionaryAsync(x => x.Id, x => x.Name);
        return ServiceResult<ImageReplicationSettings>.Ok(new(providers, targets, copies, versions));
    }

    public async Task<ServiceResult> SaveAsync(int imageId, IEnumerable<int> destinations)
    {
        var user = await ImagingPermissions.BrowserAsync(authentication, authorization, Constants.Permissions.ImageEdit);
        if (user is null) return ServiceResult.Fail("Image.Forbidden");
        var ids = destinations.Distinct().ToArray();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await StorageProviderAdministration.LockAsync(db);
        var image = await db.Images.SingleOrDefaultAsync(x => x.Id == imageId);
        if (image is null) return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageAdministration.ImageNotFound));
        var targets = await db.Set<ImageReplicationTarget>().Where(x => x.ImageId == imageId).ToListAsync();
        var added = ids.Except(targets.Select(x => x.StorageProviderId)).ToArray();
        if (await db.StorageProviders.CountAsync(x => added.Contains(x.Id) && x.IsEnabled && (x.Purposes & StoragePurpose.Imaging) != 0) != added.Length)
            return ServiceResult.Fail("Image.Validation", UiText.Get(LocKeys.Ui.ImageReplicationAdministration.ChooseEnabledProvidersWithTheImagingPurposeSiteProviders));
        db.RemoveRange(targets.Where(x => !ids.Contains(x.StorageProviderId)));
        foreach (var id in added) db.Add(new ImageReplicationTarget { ImageId = imageId, StorageProviderId = id });
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "Administrator",
            AuditType = AuditType.Update, ObjectType = nameof(Image), ObjectId = imageId, ObjectName = image.Name,
            ObjectJson = JsonSerializer.Serialize(new { Section = "Replication", Destinations = ids }) });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }
}
