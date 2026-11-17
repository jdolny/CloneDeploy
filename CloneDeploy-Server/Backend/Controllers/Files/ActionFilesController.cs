using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Files;

[ApiController]
[Authorize(Policy = Constants.Permissions.PolicyView)]
[Route("api/ui/action-files")]
public sealed class ActionFilesController(ActionFileDownloadService service) : ControllerBase
{
    [HttpGet("{actionGuid:guid}/{id:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download([FromRoute(Name = "actionGuid")] Guid action, int id, CancellationToken ct)
    {
        var result = await service.DownloadAsync(action, id, ct);
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", result.Data.Name, enableRangeProcessing: true) : this.ServiceError(result);
    }
}
