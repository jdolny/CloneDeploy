using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Reads authenticated FFU byte ranges and validates their offsets, length, and image version.
/// </summary>
internal sealed partial class ImagingClient
{
    private const int MaximumFfuReadBytes = 16 * 1024 * 1024;

    public async Task<byte[]> ReadDeploymentFfuRangeAsync(DeploymentExecution image, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count is < 1 or > MaximumFfuReadBytes || offset > image.SizeBytes - count) throw new ArgumentOutOfRangeException(nameof(offset));
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/imaging/deployment/ffu");
        request.Headers.Authorization = new("Bearer", deploymentToken ?? throw new SessionExpiredException());
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        var etag = '"' + image.Sha256.ToLowerInvariant() + '"';
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        ImagerResponseVerification.MarkDownload(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new SessionExpiredException();
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != offset ||
            range.To != offset + count - 1 || range.Length != image.SizeBytes || response.Content.Headers.ContentLength != count ||
            response.Headers.ETag?.ToString() != etag || response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException($"Invalid FFU range response (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, deadline.Token);
        if (await stream.ReadAsync(new byte[1], deadline.Token) != 0) throw new InvalidDataException("FFU range exceeded requested length.");
        return bytes;
    }
}
