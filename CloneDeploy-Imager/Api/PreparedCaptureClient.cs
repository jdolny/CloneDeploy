using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Imager.Api;

internal sealed partial class ImagingClient
{
    private PreparedCaptureHandoff? preparedCapture;
    private CaptureUpload? preparedUpload;

    public async Task PrepareCaptureAsync(PreparedCaptureHandoff handoff, CancellationToken ct)
    {
        if (handoff.UploadId == Guid.Empty || handoff.ImageId <= 0 || handoff.Token.Length != 64
            || !handoff.Token.All(Uri.IsHexDigit) || handoff.ExpiresAtUtc <= DateTime.UtcNow
            || handoff.Format is not ("Wim" or "Ffu")
            || Configuration.ImagerSettings.ServerAddress(handoff.ServerUrl) != http.BaseAddress)
            throw new InvalidOperationException("Capture handoff is invalid, expired, or belongs to a different server.");
        preparedCapture = handoff;
        using var response = await UploadRequest(HttpMethod.Get, $"uploads/{handoff.UploadId}", null, ct);
        preparedUpload = await response.Content.ReadFromJsonAsync<CaptureUpload>(ct);
        if (preparedUpload is null || preparedUpload.Id != handoff.UploadId || preparedUpload.Format != handoff.Format
            || preparedUpload.ChunkBytes is < MinimumCaptureChunkBytes or > MaximumCaptureChunkBytes)
            throw new InvalidDataException("Invalid prepared capture destination.");
    }

    private HttpRequestMessage CaptureRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        if (preparedCapture is { } capture)
        {
            var resource = path.Split('?')[0];
            var root = $"uploads/{capture.UploadId}";
            if (DateTime.UtcNow >= capture.ExpiresAtUtc || resource != root && !resource.StartsWith(root + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Capture authorization is limited to its selected upload.");
            var request = new HttpRequestMessage(method, "api/imaging/prepared-capture/" + path) { Content = content };
            request.Headers.Add("X-CloneDeploy-Capture", capture.Token);
            return request;
        }
        if (token is null || DateTimeOffset.UtcNow >= expiresAt) { SignOut(); throw new SessionExpiredException(); }
        var normal = new HttpRequestMessage(method, "api/imaging/" + path) { Content = content };
        normal.Headers.Authorization = new("Bearer", token);
        return normal;
    }
}
