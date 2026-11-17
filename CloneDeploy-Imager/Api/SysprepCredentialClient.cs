using System.Net.Http.Json;

namespace CloneDeploy_Imager.Api;

internal sealed partial class ImagingClient
{
    public async Task<Dictionary<string, string>> ResolveSysprepCredentialsAsync(string ticket, CancellationToken ct)
    {
        using var content = JsonContent.Create(ticket);
        using var response = await DeploymentRequest(HttpMethod.Post, "sysprep-credentials", content, ct);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(ct)
            ?? throw new InvalidDataException("Missing Sysprep credential response.");
    }
}
