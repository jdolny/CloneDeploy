using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Claims assigned deployments and manages their deployment-specific authentication sessions.
/// </summary>
internal sealed partial class ImagingClient
{
    private const int MaximumDeploymentSessionSeconds = 3600;

    public async Task<DeploymentAssignment?> ClaimDeploymentAsync(HardwareInventory inventory, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        using var response = await http.PostAsJsonAsync("api/imaging/deployment/claim", new
        {
            hardware = new { inventory.Uuid, inventory.Serial, inventory.Manufacturer, inventory.Model,
                MacAddresses = inventory.Networks.Select(x => x.MacAddress).ToArray() }, claimKey
        }, deadline.Token);
        log.Write($"Deployment claim HTTP {(int)response.StatusCode}.");
        if (response.StatusCode == HttpStatusCode.NoContent) { deploymentToken = null; return null; }
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            var message = problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString() : null;
            throw new DeploymentClaimConflictException(message ?? "The server could not uniquely claim this device's assigned deployment.");
        }
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<DeploymentSession>(deadline.Token);
        if (session is null || session.TokenType != "Bearer" || string.IsNullOrWhiteSpace(session.AccessToken) || !session.AccessToken.StartsWith("cdi_deploy_", StringComparison.Ordinal) ||
            session.AccessToken.Length > MaximumAccessTokenCharacters || session.AccessToken.Any(char.IsWhiteSpace) || session.ExpiresIn is <= 0 or > MaximumDeploymentSessionSeconds ||
            session.Assignment is null || session.Assignment.DeploymentId == Guid.Empty || session.Assignment.DeviceId <= 0)
            throw new InvalidDataException("The server returned an invalid deployment session.");
        deploymentToken = session.AccessToken;
        return session.Assignment;
    }
    public async Task<DeploymentAssignment> DeploymentSessionAsync(CancellationToken ct)
    {
        if (deploymentToken is null) throw new SessionExpiredException();
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/imaging/deployment/session");
        request.Headers.Authorization = new("Bearer", deploymentToken);
        using var response = await http.SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { deploymentToken = null; throw new SessionExpiredException(); }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeploymentAssignment>(deadline.Token)
            ?? throw new InvalidDataException("The server returned an invalid assignment.");
    }
    public async Task ReleaseDeploymentAsync(CancellationToken ct)
    {
        var credential = deploymentToken;
        deploymentToken = null;
        if (credential is null) return;
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/imaging/deployment/release");
        request.Headers.Authorization = new("Bearer", credential);
        using var response = await http.SendAsync(request, deadline.Token);
        log.Write($"Deployment release HTTP {(int)response.StatusCode}.");
        if (response.StatusCode != HttpStatusCode.Unauthorized) response.EnsureSuccessStatusCode();
    }
}
