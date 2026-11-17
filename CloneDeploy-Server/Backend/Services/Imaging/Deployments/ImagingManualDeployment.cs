using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ManualDeploymentImageDto(int Id, string Name, string Format, string? UnavailableReason);
public sealed record ManualDeploymentProfileDto(int Id, string Name, string? UnavailableReason);
public sealed record ManualDeploymentVersionDto(Guid Id, int ImageId, int Number, string Name, bool Approved, bool Active, string? UnavailableReason);
public sealed record ManualDeploymentCatalogDto(List<ManualDeploymentImageDto> Images, List<ManualDeploymentProfileDto> Profiles)
{
    public bool CanChoose { get; init; }
    public List<ManualDeploymentVersionDto> Versions { get; init; } = [];
}
public sealed record ManualDeploymentRequestDto(string DeviceLinkToken, int ImageId, int ProfileId)
{
    public int? DriverPackId { get; init; }
    public Guid? VersionId { get; init; }
}
public sealed record ManualDriverSelectionRequestDto(int ProfileId, string? Manufacturer, string? Model);
public sealed record ManualDriverOptionDto(int Id, string Name);
public sealed record ManualDriverSelectionDto(int? SelectedId, List<ManualDriverOptionDto> Options, string? Warning);

public sealed partial class ImagingDeploymentService
{
    public async Task<ManualDeploymentCatalogDto> ManualCatalogForDeviceAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct)
    {
        if (user.FindFirstValue("imaging_kind") != "user" || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImagerDeploy, ct)) throw new UnauthorizedAccessException();
        var canChoose = await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImagerOverride, ct) &&
            await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageView, ct) &&
            await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.DeploymentProfileView, ct);
        if (canChoose)
        {
            var catalog = await ManualCatalogAsync(ct);
            return catalog with { CanChoose = true };
        }
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct)
            ?? throw new InvalidOperationException("Device is unavailable.");
        var assigned = await ImagingInheritance.ResolveAsync(db, device);
        if (assigned.Error is not null || assigned.ImageId is null || assigned.ProfileId is null)
            throw new InvalidOperationException(assigned.Error ?? "Assign an image and deployment profile to this device before deploying.");
        var image = await db.Images.SingleAsync(x => x.Id == assigned.ImageId && x.Enabled, ct);
        var profile = await db.ImageProfiles.SingleAsync(x => x.Id == assigned.ProfileId && x.Enabled, ct);
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == image.Id && x.IsActive, ct);
        var reason = ManualImageUnavailable(version);
        if (version is { IsApproved: false } && !await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.ImageDeployUnapproved, ct))
            reason = "The assigned version is not approved.";
        return new([new(image.Id, image.Name, image.Format.ToString(), reason)], [new(profile.Id, profile.Name, null)]);
    }

    public async Task<ManualDeploymentCatalogDto> ManualCatalogAsync(CancellationToken ct)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        await using var db = await factory.CreateContextAsync();
        foreach (var permission in new[] { Constants.Permissions.ImagerDeploy, Constants.Permissions.ImagerOverride,
                     Constants.Permissions.ImageView, Constants.Permissions.DeploymentProfileView })
            if (!await ManualPermissionAsync(db, principal, permission, ct)) throw new UnauthorizedAccessException();
        var canDeployUnapproved = await ManualPermissionAsync(db, principal, Constants.Permissions.ImageDeployUnapproved, ct);
        var images = await db.Images.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Name).ToListAsync(ct);
        var versions = await db.ImageVersions.AsNoTracking()
            .Where(x => x.IsActive && x.Image!.Enabled).ToListAsync(ct);
        var profiles = await db.ImageProfiles.AsNoTracking().Where(x => x.Enabled && x.VisibleInWinPe).OrderBy(x => x.Name).ToListAsync(ct);
        var catalog = new List<ManualDeploymentImageDto>();
        foreach (var image in images)
        {
            var version = versions.SingleOrDefault(v => v.ImageId == image.Id);
            var reason = ManualImageUnavailable(version);
            if (version is { IsApproved: false } && !canDeployUnapproved) reason = "The active version is not approved.";
            if (reason is null && !await ImageReplicaCatalog.Providers(db, version!).AnyAsync(ct))
                reason = "No eligible storage provider holds this version.";
            catalog.Add(new(image.Id, image.Name, image.Format.ToString(), reason));
        }
        var choices = await db.ImageVersions.AsNoTracking().Where(x => x.Image!.Enabled && (x.IsApproved || canDeployUnapproved))
            .OrderByDescending(x => x.Number).ToListAsync(ct);
        var versionChoices = new List<ManualDeploymentVersionDto>();
        foreach (var version in choices)
        {
            var reason = ManualImageUnavailable(version);
            if (reason is null && !await ImageReplicaCatalog.Providers(db, version).AnyAsync(ct))
                reason = "No eligible storage provider holds this version.";
            versionChoices.Add(new(version.Id, version.ImageId, version.Number, version.Name, version.IsApproved, version.IsActive, reason));
        }
        catalog = catalog.Select(image => versionChoices.Any(x => x.ImageId == image.Id && x.UnavailableReason is null)
            ? image with { UnavailableReason = null } : image).ToList();
        return new(catalog, profiles.Select(x => new ManualDeploymentProfileDto(x.Id, x.Name, null)).ToList())
        {
            CanChoose = true,
            Versions = versionChoices
        };
    }

    public async Task<ManualDriverSelectionDto> ManualDriversAsync(ManualDriverSelectionRequestDto request, CancellationToken ct)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        await using var db = await factory.CreateContextAsync();
        foreach (var permission in new[] { Constants.Permissions.ImagerDeploy, Constants.Permissions.ImagerOverride,
                     Constants.Permissions.DeploymentProfileView, Constants.Permissions.DriverPackView })
            if (!await ManualPermissionAsync(db, principal, permission, ct)) throw new UnauthorizedAccessException();
        var profile = await db.ImageProfiles.SingleAsync(x => x.Id == request.ProfileId && x.Enabled && x.VisibleInWinPe, ct);
        if (profile.DriverMode != DeploymentDriverMode.Automatic)
            return new(profile.DriverMode == DeploymentDriverMode.Specific ? profile.DriverPackId : 0, [], null);
        var packs = await db.DriverProfiles.Where(x => x.Enabled).Include(x => x.Models).Include(x => x.RegexRules).AsSplitQuery().OrderBy(x => x.Name).ToListAsync(ct);
        var matches = packs.Where(x => DriverMatching.MatchesPack(x, request.Manufacturer, request.Model)).ToList();
        return matches.Count == 1 ? new(matches[0].Id, [], null)
            : new(null, packs.Select(x => new ManualDriverOptionDto(x.Id, x.Name)).ToList(),
                matches.Count == 0 ? "No matching driver pack was found. Select a driver pack." : "Multiple driver packs match. Select the pack to use.");
    }

    public async Task<ServiceResult<ImagingDeployment>> PrepareFromImagerAsync(ClaimsPrincipal user, int deviceId, int imageId, int profileId, CancellationToken ct)
        => await PrepareFromImagerAsync(user, deviceId, imageId, profileId, ct, null, null);

    public async Task<ServiceResult<ImagingDeployment>> PrepareFromImagerAsync(ClaimsPrincipal user, int deviceId, int imageId, int profileId, CancellationToken ct, int? driverPackId, Guid? versionId)
    {
        if (user.FindFirstValue("imaging_kind") != "user" || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        if (!await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagerDeploy, ct))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct);
        if (device is null) return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        var assigned = await ImagingInheritance.ResolveAsync(db, device);
        var isAssigned = assigned.Error is null && assigned.ImageId == imageId && assigned.ProfileId == profileId;
        if (!isAssigned && (!await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagerOverride, ct) ||
            !await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImageView, ct) ||
            !await ImagingPermissions.HasAsync(db, id, Constants.Permissions.DeploymentProfileView, ct)))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        if (driverPackId.HasValue && (!await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagerOverride, ct) ||
            !await ImagingPermissions.HasAsync(db, id, Constants.Permissions.DriverPackView, ct)))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        if (versionId.HasValue && (!await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagerOverride, ct) ||
            !await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImageView, ct)))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        return await PrepareCoreAsync(deviceId, imageId, profileId, user, null, isAssigned, driverPackId, null, versionId);

    }

    private async Task<bool> ManualPermissionAsync(CloneDeployDbContext db, ClaimsPrincipal user, string permission, CancellationToken ct)
    {
        // ImagingBearer has already validated its token, security stamp and imaging MFA.
        // Browser policies require browser-session claims that it intentionally does not carry.
        if (user.Identity?.AuthenticationType == CloneDeploy_Server.Backend.Auth.ImagingAuthentication.SchemeName)
            return user.FindFirstValue("imaging_kind") == "user" &&
                int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) &&
                await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagingAccess, ct) &&
                await ImagingPermissions.HasAsync(db, id, permission, ct);
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded;
    }

    private static string? ManualImageUnavailable(ImageVersion? version)
    {
        if (version is null) return "No active version.";
        if (version.SizeBytes <= 0 || version.Sha256 is not { Length: 64 } || !version.Sha256.All(Uri.IsHexDigit)) return "Capture metadata is missing.";
        if (version.Format == ImageFormat.Wim && version.Pipable == true && version.Layout == "WindowsPartition" && version.FileName == "image.wim") return null;
        if (version.Format != ImageFormat.Ffu || version.Layout != "WholeDisk" || version.FileName != "image.ffu") return "Unsupported image format or capture metadata.";
        return null;
    }
}
