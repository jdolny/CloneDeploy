using System.Net.Http.Json;
namespace CloneDeploy_Imager.Api;

/// <summary>
/// Describes an image choice and any reason it cannot currently be deployed.
/// </summary>
internal sealed record ManualDeploymentImage(int Id, string Name, string Format, string? UnavailableReason);
/// <summary>
/// Describes a deployment profile choice and any reason it cannot currently be used.
/// </summary>
internal sealed record ManualDeploymentProfile(int Id, string Name, string? UnavailableReason);
/// <summary>
/// Groups the images and profiles available for an operator-selected deployment.
/// </summary>
internal sealed record ManualDeploymentVersion(Guid Id, int ImageId, int Number, string Name, bool Approved, bool Active, string? UnavailableReason);
internal sealed record ManualDeploymentCatalog(List<ManualDeploymentImage> Images, List<ManualDeploymentProfile> Profiles)
{
    public bool CanChoose { get; init; }
    public List<ManualDeploymentVersion> Versions { get; init; } = [];
}
/// <summary>
/// Returns the identifier of a deployment prepared for the linked device.
/// </summary>
internal sealed record PreparedDeployment(Guid DeploymentId);
internal sealed record ManualDriverOption(int Id, string Name);
internal sealed record ManualDriverSelection(int? SelectedId, List<ManualDriverOption> Options, string? Warning);

/// <summary>
/// Retrieves deployable images and profiles and prepares a deployment selected by the operator.
/// </summary>
internal sealed partial class ImagingClient
{
    public async Task<ManualDeploymentCatalog> DeploymentCatalogAsync(CancellationToken ct)
    {
        if (Device is null) throw new InvalidOperationException("Link this session to a device before deploying.");
        using var content = JsonContent.Create(new { deviceLinkToken = Device.LinkToken });
        using var response = await UploadRequest(HttpMethod.Post, "deployment/catalog", content, ct);
        var catalog = await response.Content.ReadFromJsonAsync<ManualDeploymentCatalog>(ct);
        return catalog is { Images: not null, Profiles: not null } ? catalog : throw new InvalidDataException("Missing deployment catalog.");
    }
    public async Task<Guid> PrepareDeploymentAsync(int imageId, int profileId, CancellationToken ct)
        => await PrepareDeploymentAsync(imageId, profileId, null, ct);

    public async Task<ManualDriverSelection> SelectDriversAsync(int profileId, string? manufacturer, string? model, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { profileId, manufacturer, model });
        using var response = await UploadRequest(HttpMethod.Post, "deployment/driver-selection", content, ct);
        return await response.Content.ReadFromJsonAsync<ManualDriverSelection>(ct) ?? throw new InvalidDataException("Missing driver selection.");
    }

    public async Task<Guid> PrepareDeploymentAsync(int imageId, int profileId, int? driverPackId, CancellationToken ct)
        => await PrepareSelectedVersionAsync(imageId, profileId, driverPackId, null, ct);

    public async Task<Guid> PrepareSelectedVersionAsync(int imageId, int profileId, int? driverPackId, Guid? versionId, CancellationToken ct)
    {
        if (Device is null) throw new InvalidOperationException("Link this session to a device before deploying.");
        using var content = JsonContent.Create(new { deviceLinkToken = Device.LinkToken, imageId, profileId, driverPackId, versionId });
        using var response = await UploadRequest(HttpMethod.Post, "deployment/prepare", content, ct);
        var result = await response.Content.ReadFromJsonAsync<PreparedDeployment>(ct);
        return result is not null && result.DeploymentId != Guid.Empty ? result.DeploymentId : throw new InvalidDataException("Missing prepared deployment.");
    }
}
