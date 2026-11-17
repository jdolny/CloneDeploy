using System.IO.Compression;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds original or prepared driver files and checks whether they can be downloaded.
/// </summary>
public sealed class DriverFileDownloadService(ICloneDeployDbFactory factory, StoredFileStorage storage, DriverPreparationStorage prepared, ILogger<DriverFileDownloadService> logger)
{
    public SharedContentDelivery? Shared { get; init; }

    public Task<ServiceResult<PhysicalDownloadDto>> DownloadPreparedAsync(int profileId, int id, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadPreparedCoreAsync(profileId, id, ct), logger);

    private async Task<ServiceResult<PhysicalDownloadDto>> DownloadPreparedCoreAsync(int profileId, int id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var link = await db.DriverFiles.AsNoTracking().Include(x => x.File)
            .SingleOrDefaultAsync(x => x.DriverProfileId == profileId && x.StoredFileId == id, ct);
        if (link?.PreparedArchiveId is not { } archiveId) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        var path = Shared is null ? prepared.ArchivePath(archiveId, link.PreparedStorageProviderId) :
            await Shared.RequestPathAsync(CloneDeploy_Server.Backend.Data.Models.SharedContentKind.DriverArchive, archiveId.ToString("N"), link.PreparedSha256,
                () => Task.FromResult(prepared.ArchivePath(archiveId, link.PreparedStorageProviderId)), ct);
        if (path != SiteDownloadDelivery.PathMarker && !System.IO.File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        return ServiceResult<PhysicalDownloadDto>.Ok(new(path, Path.GetFileNameWithoutExtension(link.File.Name) + "-prepared.zip"));
    }

    public Task<ServiceResult<PhysicalDownloadDto>> DownloadAsync(int profileId, int id, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadCoreAsync(profileId, id, ct), logger);

    private async Task<ServiceResult<PhysicalDownloadDto>> DownloadCoreAsync(int profileId, int id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var file = await db.DriverFiles.Where(x => x.DriverProfileId == profileId && x.StoredFileId == id)
            .Select(x => x.File).SingleOrDefaultAsync(ct);
        if (file is null) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        var path = Shared is null ? storage.NamedPath(file) : await Shared.RequestPathAsync(CloneDeploy_Server.Backend.Data.Models.SharedContentKind.DriverOriginal,
            file.Id.ToString(), file.Sha256Hash, () => Task.FromResult(storage.NamedPath(file)), ct);
        if (path != SiteDownloadDelivery.PathMarker && !System.IO.File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        return ServiceResult<PhysicalDownloadDto>.Ok(new(path, file.Name));
    }
}
