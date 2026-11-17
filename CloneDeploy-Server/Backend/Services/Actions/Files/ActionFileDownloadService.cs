using System.IO.Compression;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds files attached to an action and checks whether they can be downloaded.
/// </summary>
public sealed class ActionFileDownloadService(ICloneDeployDbFactory factory, StoredFileStorage storage, ILogger<ActionFileDownloadService> logger)
{
    public SharedContentDelivery? Shared { get; init; }
    public Task<ServiceResult<PhysicalDownloadDto>> DownloadAsync(Guid action, int id, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadCoreAsync(action, id, ct), logger);

    private async Task<ServiceResult<PhysicalDownloadDto>> DownloadCoreAsync(Guid action, int id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var file = await db.ActionFiles.Where(x => x.ActionGuid == action && x.StoredFileId == id)
            .Select(x => x.File).SingleOrDefaultAsync(ct);
        if (file is null) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        async Task<string> PrimaryAsync() { await storage.EnsureReadyAsync(file, ct); return storage.NamedPath(file); }
        var path = Shared is null ? await PrimaryAsync() : await Shared.RequestPathAsync(
            CloneDeploy_Server.Backend.Data.Models.SharedContentKind.ManagementFile, file.Id.ToString(), file.Sha256Hash, PrimaryAsync, ct);
        if (path != SiteDownloadDelivery.PathMarker && !System.IO.File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        return ServiceResult<PhysicalDownloadDto>.Ok(new(path, file.Name));
    }
}
