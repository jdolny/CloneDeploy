using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Imager.Api;

internal sealed partial class ImagingClient
{
    public async Task<ImagingOuPlan> ResolveDeploymentOuAsync(ImagingOuRequest request, CancellationToken ct)
    {
        using var content = JsonContent.Create(request);
        using var response = await DeploymentRequest(HttpMethod.Post, "ou", content, ct);
        return await response.Content.ReadFromJsonAsync<ImagingOuPlan>(ct)
            ?? throw new InvalidDataException("The OU resolution response was empty.");
    }
}
