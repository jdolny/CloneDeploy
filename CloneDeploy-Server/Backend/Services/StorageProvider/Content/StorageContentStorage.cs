using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// The default selects destinations for new content. Existing content retains its provider ID.
/// <summary>
/// Finds available storage locations and the paths used to store application files.
/// </summary>
public sealed class StorageContentStorage(IServiceScopeFactory scopes, StorageMountCoordinator mounts, StorageMountPaths paths)
{
    // This selects a destination for NEW content only. Existing reads must use a
    // recorded, verified copy; an unrelated reachable provider is not a replica.
    public async Task<int> SelectProviderAsync(StoragePurpose purpose, CancellationToken ct = default)
    {
        if (purpose is not (StoragePurpose.Imaging or StoragePurpose.DeviceManagement))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var candidates = await db.StorageProviders.AsNoTracking()
            .Where(x => x.IsEnabled && x.SiteServiceId == null && (x.Purposes & purpose) == purpose)
            .OrderByDescending(x => purpose == StoragePurpose.Imaging ? x.IsImagingDefault : x.IsManagementDefault).ThenBy(x => x.Id)
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // Refresh cached health before choosing a write destination.
            await mounts.CheckAsync(id, ct);
            if (IsReady(id)) return id;
        }
        throw new IOException($"No reachable main-app storage provider supports {purpose}. Check provider purposes and connections in Settings > Storage.");
    }

    public async Task EnsureReadyAsync(int id, CancellationToken ct = default)
    {
        if (!IsReady(id)) await mounts.CheckAsync(id, ct);
        GetRoot(id);
    }

    public bool IsReady(int id) => mounts.GetStatuses().Any(x => x.ProviderId == id && x.Mounted && x.Writable);

    public string GetRoot(int id)
    {
        mounts.VerifyLinuxMount(id);
        if (!IsReady(id) || new DirectoryInfo(paths.ProviderPath(id)).LinkTarget is null)
            throw new IOException($"Storage provider {id} is unavailable. Check its connection in Settings > Storage.");
        return paths.ProviderPath(id);
    }

}
