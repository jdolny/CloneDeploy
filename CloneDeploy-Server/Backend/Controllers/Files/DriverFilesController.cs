using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Files;

[ApiController]
[Authorize(Policy = Constants.Permissions.DriverPackView)]
[Route("api/ui/driver-files")]
public sealed class DriverFilesController(DriverFileDownloadService service) : ControllerBase
{
    [HttpGet("{profileId:int}/{id:int}/prepared")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> DownloadPrepared(int profileId, int id, CancellationToken ct)
    {
        var result = await service.DownloadPreparedAsync(profileId, id, ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/zip", result.Data.Name, enableRangeProcessing: true) : this.ServiceError(result);
    }

    [HttpGet("{profileId:int}/{id:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download(int profileId, int id, CancellationToken ct)
    {
        var result = await service.DownloadAsync(profileId, id, ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", result.Data.Name, enableRangeProcessing: true) : this.ServiceError(result);
    }
}
