using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Authorize(Policy = Constants.Permissions.AdminOnly)]
[Route("api/ui/winpe/builds")]
public sealed class WinPeController(WinPeDownloadService service) : ControllerBase
{
    [HttpGet("{id:guid}/{name}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download(Guid id, string name, CancellationToken ct)
    {
        var result = await service.DownloadAsync(id, name, ct);
        return result.Success ? PhysicalFile(result.Data!.Path, name.EndsWith(".json") ? "application/json" : name.EndsWith(".log") ? "text/plain" : "application/octet-stream", result.Data.Name, enableRangeProcessing: true) : this.ServiceError(result);
    }
}
