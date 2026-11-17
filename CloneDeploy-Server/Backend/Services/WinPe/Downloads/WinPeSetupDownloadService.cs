using System.IO.Compression;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.WinPe;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Prepares builder setup logs for download.
/// </summary>
public sealed class WinPeSetupDownloadService(ICloneDeployDbFactory factory, BuildStorage storage, ILogger<WinPeSetupDownloadService> logger)
{
    public Task<ServiceResult<byte[]>> LogsAsync(Guid id, CancellationToken ct) =>
        ServiceOperations.RunAsync(() => LogsCoreAsync(id, ct), logger);

    private async Task<ServiceResult<byte[]>> LogsCoreAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (!await db.WinPeSetupJobs.AnyAsync(x => x.Id == id, ct)) return ServiceResult<byte[]>.Fail(ServiceErrorCodes.NotFound);
        var directory = SetupFiles.DirectoryPath(storage, id);
        if (!Directory.Exists(directory)) return ServiceResult<byte[]>.Fail(ServiceErrorCodes.NotFound);
        BuildStorage.RejectLinks(directory);
        // Include bootstrapper and child MSI logs, never executables or arbitrary paths.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                BuildStorage.RejectLinks(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = zip.CreateEntry(Path.GetFileName(path)).Open(); await input.CopyToAsync(output, ct);
            }
        }
        return ServiceResult<byte[]>.Ok(buffer.ToArray());
    }
}
