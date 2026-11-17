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
/// Creates capture destinations and manages upload chunks, progress, completion, and cancellation.
/// </summary>
internal sealed partial class ImagingClient
{
    public async Task<List<CaptureImage>> CaptureImagesAsync(CancellationToken ct)
    {
        using var response = await UploadRequest(HttpMethod.Get, "capture-images", null, ct);
        return await response.Content.ReadFromJsonAsync<List<CaptureImage>>(ct) ?? throw new InvalidDataException("Missing image catalog.");
    }

    public async Task<CaptureImage> CreateCaptureImageAsync(string name, string format, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { name, format });
        using var response = await UploadRequest(HttpMethod.Post, "capture-images", content, ct);
        var image = await response.Content.ReadFromJsonAsync<CaptureImage>(ct);
        if (image is null || image.Id <= 0 || string.IsNullOrWhiteSpace(image.Name) || image.Format != format)
            throw new InvalidDataException("The server returned an invalid image.");
        return image;
    }

    public async Task<CaptureUpload> BeginCaptureAsync(int imageId, CancellationToken ct, string format = "Wim")
    {
        if (preparedCapture is { } prepared)
        {
            if (prepared.ImageId != imageId || prepared.Format != format || preparedUpload is null)
                throw new InvalidOperationException("Capture destination does not match the prepared task.");
            return preparedUpload;
        }
        if (Device is null) throw new InvalidOperationException("Link this session to a device before capturing.");
        using var content = JsonContent.Create(new { deviceLinkToken = Device.LinkToken, format });
        using var response = await UploadRequest(HttpMethod.Post, $"images/{imageId}/uploads", content, ct);
        var result = await response.Content.ReadFromJsonAsync<CaptureUpload>(ct);
        if (result is null || result.Id == Guid.Empty || result.ChunkBytes is < MinimumCaptureChunkBytes or > MaximumCaptureChunkBytes)
            throw new InvalidDataException("Invalid capture upload settings.");
        return result;
    }

    public async Task UploadChunkAsync(Guid id, long offset, byte[] bytes, int count, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes, 0, count);
        content.Headers.ContentType = new("application/octet-stream");
        using var response = await UploadRequest(HttpMethod.Put, $"uploads/{id}/content?offset={offset}", content, ct);
    }

    private Guid? completedCaptureId;

    public async Task<CaptureResult> CompleteCaptureAsync(Guid id, long size, CancellationToken ct, bool optimized = false, long? minimumDiskSizeBytes = null)
    {
        using var content = JsonContent.Create(new { sizeBytes = size, optimized, minimumDiskSizeBytes });
        using var response = await UploadRequest(HttpMethod.Post, $"uploads/{id}/complete", content, ct, TimeSpan.FromHours(2));
        var result = await response.Content.ReadFromJsonAsync<CaptureResult>(ct) ?? throw new InvalidDataException("Missing completed image version.");
        completedCaptureId = id;
        return result;
    }

    public async Task<bool> TryUploadCaptureLogAsync()
    {
        if (completedCaptureId is not { } id) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var content = JsonContent.Create(new { contents = log.Snapshot() });
            using var response = await UploadRequest(HttpMethod.Post, $"uploads/{id}/logs", content, deadline.Token);
            return true;
        }
        catch (Exception ex) { log.Failure("Successful capture log upload could not complete", ex); return false; }
    }

    public async Task AbortCaptureAsync(Guid id, CancellationToken ct, string reason = "Cancelled")
    {
        using var response = await UploadRequest(HttpMethod.Delete, $"uploads/{id}?reason={Uri.EscapeDataString(reason)}", null, ct);
    }

    public async Task ReportCaptureAsync(Guid id, DeploymentProgress progress, CancellationToken ct)
    {
        await RenewCaptureSessionAsync(id, ct);
        using var content = JsonContent.Create(new { phase = (int)progress.Stage, percent = progress.Percent, networkBytesPerSecond = progress.NetworkBytesPerSecond, statusMessage = progress.Status });
        using var response = await UploadRequest(HttpMethod.Post, $"uploads/{id}/progress", content, ct, TimeSpan.FromSeconds(10));
    }

    private async Task RenewCaptureSessionAsync(Guid id, CancellationToken ct)
    {
        if (preparedCapture is not null || token is null || expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2)) return;
        var originalToken = token;
        using var response = await UploadRequest(HttpMethod.Post, $"uploads/{id:D}/renew", null, ct, TimeSpan.FromSeconds(10));
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
        var renewed = await response.Content.ReadFromJsonAsync<SessionToken>(ct);
        if (renewed is null || renewed.TokenType != "Bearer" || string.IsNullOrWhiteSpace(renewed.AccessToken) ||
            renewed.AccessToken.Any(char.IsWhiteSpace) || renewed.ExpiresIn is <= 0 or > MaximumLoginSessionSeconds)
            throw new InvalidDataException("The server returned an invalid renewed imaging session.");
        // A concurrent rejection/sign-out must not be undone by a late renewal reply.
        lock (sessionGate)
        {
            if (token != originalToken) throw new SessionExpiredException();
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(renewed.ExpiresIn);
            token = renewed.AccessToken;
        }
        log.Write("Active capture session renewed.");
    }
}
