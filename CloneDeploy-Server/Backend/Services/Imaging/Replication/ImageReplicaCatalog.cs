using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public static class ImageReplicaCatalog
{
    // Configuration eligibility; mount availability is checked when reserving a
    // transfer. The original provider remains the authoritative write location.
    public static IQueryable<StorageProvider> Providers(CloneDeployDbContext db, ImageVersion version) =>
        db.StorageProviders.Where(p => p.IsEnabled && (p.SiteServiceId == null || p.SiteService!.State == SiteRegistrationState.Approved && p.SiteService.ProxyEnabled) && (p.Purposes & StoragePurpose.Imaging) != 0 &&
            (version.Format != ImageFormat.Ffu || p.Type != StorageProviderType.Smb || !p.ImagingDirectConnect ||
             p.ManagedCredential != null && p.ManagedCredential.Username != "") &&
            (p.Id == version.StorageProviderId || db.Set<ImageReplica>().Any(r => r.ImageVersionId == version.Id &&
                r.StorageProviderId == p.Id && r.State == ImageReplicaState.Ready &&
                db.Set<ImageReplicationTarget>().Any(t => t.ImageId == r.ImageId && t.StorageProviderId == r.StorageProviderId))));
}
