using System.Net.Http.Json;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Acknowledges an FFU mutation with its sequence, fingerprint, existence flag, and resulting file length.
/// </summary>
internal sealed record FfuFileState(long Sequence, string Fingerprint, bool Exists, long Length);
/// <summary>
/// Sends ordered FFU file mutations with retry receipts and reads back unfinished upload data.
/// </summary>
internal sealed partial class ImagingClient
{
    private const long MaximumFfuUploadBytes = 2L * 1024 * 1024 * 1024 * 1024;
    private const int FfuMutationRetries = 2;
    private const int FfuRetryDelayMilliseconds = 250;

    public async Task<FfuFileState> MutateFfuAsync(Guid id, long sequence, string operation, long offset, byte[] bytes, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/octet-stream");
                using var response = await UploadRequest(HttpMethod.Post, $"uploads/{id:D}/ffu?sequence={sequence}&operation={operation}&offset={offset}", content, ct);
                var state = await response.Content.ReadFromJsonAsync<FfuFileState>(ct);
                if (state is null || state.Sequence != sequence || !state.Exists || state.Length < 0 || state.Length > MaximumFfuUploadBytes)
                    throw new InvalidDataException("Invalid FFU write acknowledgement.");
                return state;
            }
            catch (Exception ex) when (attempt < FfuMutationRetries && !ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            { await Task.Delay(FfuRetryDelayMilliseconds * (attempt + 1), ct); }
        }
    }
    public async Task<byte[]> ReadFfuUploadAsync(Guid id, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count is < 1 or > MaximumCaptureChunkBytes) throw new ArgumentOutOfRangeException(nameof(count));
        using var deadline = Deadline(ct);
        using var request = CaptureRequest(HttpMethod.Get, $"uploads/{id:D}/ffu?offset={offset}&count={count}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new SessionExpiredException();
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength != count || response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException($"Invalid FFU read-back response (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, deadline.Token);
        if (await stream.ReadAsync(new byte[1], deadline.Token) != 0) throw new InvalidDataException("FFU read-back exceeds requested length.");
        return bytes;
    }
}
