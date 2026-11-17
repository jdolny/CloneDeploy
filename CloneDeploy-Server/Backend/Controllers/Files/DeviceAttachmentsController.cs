using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloneDeploy_Server.Backend.Controllers.Files;

[ApiController]
[Authorize(Policy = Constants.Permissions.DeviceView)]
[Route("api/ui/devices/{deviceId:int}/attachments")]
public sealed class DeviceAttachmentsController(DeviceAttachmentService attachments) : ControllerBase
{
    [HttpGet("{attachmentId:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Download(int deviceId, int attachmentId, CancellationToken ct)
    {
        var result = await attachments.DownloadAsync(User, deviceId, attachmentId, ct);
        return result.Success
            ? PhysicalFile(result.Data!.Path, "application/octet-stream", result.Data.Name, enableRangeProcessing: true)
            : this.ServiceError(result);
    }
}
