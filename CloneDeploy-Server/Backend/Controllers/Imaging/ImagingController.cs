using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Controllers.Common;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Route("api/imaging"), Authorize(Policy = ImagingAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ImagingController(ImagingBootService boot, ImagingLoginService login) : ControllerBase
{
    private const int MaxRequestBytes = 8192;

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot"), HttpHead("/api/boot"), EnableRateLimiting("ImagingBoot")]
    public async Task<IActionResult> Boot([FromQuery] ImagingBootRequestDto request, CancellationToken ct)
    {
        // Relative file URLs must resolve correctly with either spelling of the endpoint.
        var result = Request.Path.Value?.EndsWith('/') == true
            ? await boot.IpxeAsync(request, ct) : await boot.BootAsync(request, ct);
        return result.Success ? Content(result.Data!, "text/plain") : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/ipxe"), HttpHead("/api/boot/ipxe"), EnableRateLimiting("ImagingBoot")]
    public async Task<IActionResult> Ipxe([FromQuery] ImagingBootRequestDto request, CancellationToken ct)
    {
        var result = await boot.IpxeAsync(request, ct);
        return result.Success ? Content(result.Data!, "text/plain") : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/wimboot"), HttpHead("/api/boot/wimboot"), EnableRateLimiting("ImagingBoot")]
    public IActionResult Wimboot()
    {
        var result = boot.Wimboot();
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true) : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/boot.wim"), HttpHead("/api/boot/files/{id:guid}/boot.wim"), EnableRateLimiting("ImagingBoot")]
    public async Task<IActionResult> BootWim(Guid id, CancellationToken ct)
    {
        var result = await boot.BootWimAsync(id, ct);
        if (result.Success) return PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true);
        return result.Error.Code == ServiceErrorCodes.Unavailable
            ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Boot media storage is unavailable.") : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/BCD"), HttpHead("/api/boot/files/{id:guid}/BCD"), EnableRateLimiting("ImagingBoot")]
    public Task<IActionResult> BootBcd(Guid id, CancellationToken ct) => BootResource(id, "BCD", ct);

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/boot.sdi"), HttpHead("/api/boot/files/{id:guid}/boot.sdi"), EnableRateLimiting("ImagingBoot")]
    public Task<IActionResult> BootSdi(Guid id, CancellationToken ct) => BootResource(id, "boot.sdi", ct);

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/bootmgr"), HttpHead("/api/boot/files/{id:guid}/bootmgr"), EnableRateLimiting("ImagingBoot")]
    public Task<IActionResult> BootManager(Guid id, CancellationToken ct) => BootResource(id, "bootmgr", ct);

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/bootmgfw.efi"), HttpHead("/api/boot/files/{id:guid}/bootmgfw.efi"), EnableRateLimiting("ImagingBoot")]
    public Task<IActionResult> BootEfiManager(Guid id, CancellationToken ct) => BootResource(id, "bootmgfw.efi", ct);

    private async Task<IActionResult> BootResource(Guid id, string name, CancellationToken ct)
    {
        var result = await boot.BootResourceAsync(id, name, ct);
        if (result.Success) return PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true);
        return result.Error.Code == ServiceErrorCodes.Unavailable
            ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "WinPE boot resources are unavailable. Rebuild the boot environment if its standalone files are missing.")
            : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/wimboot.arm64"), HttpHead("/api/boot/wimboot.arm64"), EnableRateLimiting("ImagingBoot")]
    public IActionResult Arm64Wimboot()
    {
        var result = boot.WimbootFor("arm64");
        return result.Success ? PhysicalFile(result.Data!.Path, "application/octet-stream", enableRangeProcessing: true) : this.ServiceError(result);
    }

    [AllowAnonymous, AllowHttpBoot, HttpGet("/api/boot/files/{id:guid}/arm64/{name}"), HttpHead("/api/boot/files/{id:guid}/arm64/{name}"), EnableRateLimiting("ImagingBoot")]
    public Task<IActionResult> Arm64Resource(Guid id, string name, CancellationToken ct)
    {
        var source = name switch { "boot.wim" => "boot-arm64.wim", "BCD" => "BCD-arm64", "boot.sdi" => "boot-arm64.sdi", "bootmgfw.efi" => "bootaa64.efi", _ => null };
        return source is null ? Task.FromResult<IActionResult>(NotFound()) : BootResource(id, source, ct);
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("ImagingLogin"), RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Login(ImagingLoginRequestDto request)
    {
        if (!Request.IsHttps) return Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Login requires HTTPS.");
        var result = await login.LoginAsync(request);
        if (result.Success) return Ok(result.Data);
        return result.Error.Code switch
        {
            "Imaging.MfaEnrollmentRequired" => StatusCode(StatusCodes.Status403Forbidden, new { error = "mfa_enrollment_required" }),
            "Imaging.MfaRequired" => Unauthorized(new { error = "mfa_required" }),
            ServiceErrorCodes.Forbidden => Forbid(ImagingAuthentication.SchemeName),
            _ => this.ServiceError(result)
        };
    }

    [HttpGet("session")]
    public IActionResult Session() => Ok(new { subject = User.FindFirstValue(ClaimTypes.NameIdentifier), name = User.Identity?.Name,
        kind = User.FindFirstValue("imaging_kind"), permissions = User.FindAll(Constants.Claims.Permission).Select(x => x.Value).ToArray() });
}
