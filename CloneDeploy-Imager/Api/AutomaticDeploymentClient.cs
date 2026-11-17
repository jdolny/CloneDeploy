using System.Net.Http.Json;

namespace CloneDeploy_Imager.Api;

internal sealed record AutomaticDeploymentMatch(int Id, string Name, int ImageId, string ImageName,
    int ProfileId, string ProfileName, string? UnavailableReason);

internal sealed partial class ImagingClient
{
    public async Task<List<AutomaticDeploymentMatch>> AutomaticMatchesAsync(string? manufacturer, string? model, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { manufacturer, model });
        using var response = await UploadRequest(HttpMethod.Post, "deployment/automatic-matches", content, ct);
        return await response.Content.ReadFromJsonAsync<List<AutomaticDeploymentMatch>>(ct)
            ?? throw new InvalidDataException("Missing deployment rule results.");
    }
}
