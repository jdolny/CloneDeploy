using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Imaging;

[ApiController, Route("api/imaging/deployment"), Authorize(Policy = ImagingAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ImagingManualDeploymentController(ICloneDeployDbFactory factory, ImagingDeviceService devices,
    ImagingUploadService access, IAuthorizationService authorization) : ControllerBase
{
    private const int DeploymentSelectionRequestBytes = 8192;
    private const int DeviceSelectionRequestBytes = 4096;

    private ImagingDeploymentService Service => new(factory, new RequestAuthentication(User), authorization);
    private sealed class RequestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    public sealed record DeviceCatalogRequest(string DeviceLinkToken);
    [HttpPost("catalog"), RequestSizeLimit(DeploymentSelectionRequestBytes)]
    public async Task<IActionResult> DeviceCatalog(DeviceCatalogRequest request, CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try
        {
            var deviceId = devices.ReadLink(request.DeviceLinkToken, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
            return Ok(await Service.ManualCatalogForDeviceAsync(User, deviceId, ct));
        }
        catch (UnauthorizedAccessException) { return Forbid(ImagingAuthentication.SchemeName); }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try { return Ok(await Service.ManualCatalogAsync(ct)); }
        catch (UnauthorizedAccessException) { return Forbid(ImagingAuthentication.SchemeName); }
    }

    [HttpPost("prepare"), RequestSizeLimit(DeploymentSelectionRequestBytes)]
    public async Task<IActionResult> Prepare(ManualDeploymentRequestDto request, CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try
        {
            var deviceId = devices.ReadLink(request.DeviceLinkToken, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
            var result = await Service.PrepareFromImagerAsync(User, deviceId, request.ImageId, request.ProfileId, ct, request.DriverPackId, request.VersionId);
            if (!result.Success) return result.Error!.Code == "Image.Forbidden" ? Forbid(ImagingAuthentication.SchemeName) :
                Conflict(new { message = result.Error.Args.FirstOrDefault()?.ToString() ?? "Deployment could not be prepared." });
            return Ok(new { deploymentId = result.Data!.Id });
        }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("automatic-matches"), RequestSizeLimit(DeviceSelectionRequestBytes)]
    public async Task<IActionResult> AutomaticMatches(AutomaticDeploymentMatchRequest request, CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        foreach (var permission in new[] { Constants.Permissions.ImagerOverride, Constants.Permissions.ImageView, Constants.Permissions.DeploymentProfileView })
            if (!await access.AllowedAsync(User, permission, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try { return Ok(await Service.MatchAutomaticRulesAsync(request, ct)); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    public sealed record MulticastJoinRequest(string DeviceLinkToken, Guid SessionId);
    [HttpGet("multicast")]
    public async Task<IActionResult> Multicast(CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        foreach (var permission in new[] { Constants.Permissions.ImagerOverride, Constants.Permissions.ImageView, Constants.Permissions.DeploymentProfileView })
            if (!await access.AllowedAsync(User, permission, ct)) return Forbid(ImagingAuthentication.SchemeName);
        await using var db = await factory.CreateContextAsync();
        return Ok(await db.Set<MulticastSession>().Where(x => x.GroupId == null && x.State == MulticastState.Waiting && !x.StartRequested &&
            (x.SenderLease == null || x.SenderLeaseExpiresAtUtc > DateTime.UtcNow))
            .Select(x => new { x.Id, x.Name, Image = db.ImageVersions.Where(v => v.Id == x.ImageVersionId).Select(v => v.Name).First(),
                x.SenderAddress, x.ExpectedReceivers }).ToListAsync(ct));
    }
    [HttpPost("multicast/join"), RequestSizeLimit(DeploymentSelectionRequestBytes)]
    public async Task<IActionResult> JoinMulticast(MulticastJoinRequest request, CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try
        {
            var device = devices.ReadLink(request.DeviceLinkToken, int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
            var result = await Service.JoinMulticastAsync(User, device, request.SessionId, ct);
            return result.Success ? Ok(new { deploymentId = result.Data!.Id }) : Conflict(new { message = result.Error!.Args.FirstOrDefault()?.ToString() ?? "Unable to join multicast." });
        }
        catch (InvalidDataException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("driver-selection"), RequestSizeLimit(DeviceSelectionRequestBytes)]
    public async Task<IActionResult> Drivers(ManualDriverSelectionRequestDto request, CancellationToken ct)
    {
        if (!await access.AllowedAsync(User, Constants.Permissions.ImagerDeploy, ct)) return Forbid(ImagingAuthentication.SchemeName);
        try { return Ok(await Service.ManualDriversAsync(request, ct)); }
        catch (UnauthorizedAccessException) { return Forbid(ImagingAuthentication.SchemeName); }
        catch (InvalidOperationException) { return Conflict(new { message = "Deployment profile is unavailable." }); }
    }
}
