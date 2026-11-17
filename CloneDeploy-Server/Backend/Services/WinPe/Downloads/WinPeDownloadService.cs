using System.IO.Compression;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds boot environment build files and checks whether they can be downloaded.
/// </summary>
public sealed class WinPeDownloadService(ICloneDeployDbFactory factory, BuildStorage storage, WinPeArtifactStorage artifacts, ILogger<WinPeDownloadService> logger)
{
    public Task<ServiceResult<PhysicalDownloadDto>> DownloadAsync(Guid id, string name, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => DownloadCoreAsync(id, name, ct), logger);

    private async Task<ServiceResult<PhysicalDownloadDto>> DownloadCoreAsync(Guid id, string name, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var build = await db.WinPeBuilds.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (build is null) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        var status = artifacts.Status(build);
        if (name != "build.log" && name != "request.json" && (status.State != "Ready" || !status.Artifacts.Any(x => x.Name == name))) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        string path;
        try
        {
            if (build.StoredAtUtc is not null) path = await artifacts.PathAsync(build, name, ct);
            else { path = storage.FilePath(id, name); BuildStorage.RejectLinks(path); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { logger.LogWarning(ex, "WinPE build storage unavailable"); return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.Unavailable, UiText.Get(LocKeys.Ui.WinPeDownloadService.BuildStorageIsUnavailableCheckTheStorageProviderConnection)); }
        if (path != SiteDownloadDelivery.PathMarker && !System.IO.File.Exists(path)) return ServiceResult<PhysicalDownloadDto>.Fail(ServiceErrorCodes.NotFound);
        return ServiceResult<PhysicalDownloadDto>.Ok(new(path, name));
    }
}
