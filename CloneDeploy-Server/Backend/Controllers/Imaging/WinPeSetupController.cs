using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Authorize(Policy = Constants.Permissions.AdminOnly)]
[Route("api/ui/winpe/setup")]
public sealed class WinPeSetupController(WinPeSetupDownloadService service) : ControllerBase
{
    [HttpGet("{id:guid}/logs")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Logs(Guid id, CancellationToken ct)
    {
        var result = await service.LogsAsync(id, ct);
        return result.Success ? File(result.Data!, "application/zip", $"winpe-setup-{id:N}-logs.zip") : this.ServiceError(result);
    }
}
