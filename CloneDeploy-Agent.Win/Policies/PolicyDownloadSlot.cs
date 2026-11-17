using System.Net;
using System.Net.Http.Json;

namespace CloneDeploy_Agent;

/// <summary>
/// Reserves download capacity on the server and releases it when the transfer is done.
/// </summary>
internal sealed class PolicyDownloadSlot(HttpClient client, Guid id) : IAsyncDisposable
{
    private static readonly TimeSpan AcquisitionRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    public static async Task<PolicyDownloadSlot> AcquireAsync(
        HttpClient client,
        Guid runId,
        CancellationToken ct,
        string? contentBasePath = null)
    {
        for (var attempt = 0;; attempt++)
        {
            using var response = await client.PostAsync(
                $"{(contentBasePath ?? $"api/agent/v1/policies/runs/{runId}")}/download-slot",
                null,
                ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 40)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? AcquisitionRetryDelay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();

            return new(client, await response.Content.ReadFromJsonAsync<Guid>(ct));
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);

        try
        {
            using var response = await client.DeleteAsync($"api/agent/v1/policies/download-slots/{id}", timeout.Token);
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}