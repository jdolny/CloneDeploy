using System.Net;
using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Sends collected inventory to the server and waits to retry when the server is busy.
/// </summary>
public static class AgentInventorySubmission
{
    private const int MinimumRetrySeconds = 1;
    private const int MaximumRetrySeconds = 60;
    private const int RetryJitterSeconds = 5;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(30);

    public static async Task SendAsync(HttpClient client, AgentInventory inventory, CancellationToken ct)
    {
        // Keep the same collected snapshot for every retry; cancellation bounds the total wait.
        while (true)
        {
            TimeSpan delay;
            using (var response = await client.PostAsJsonAsync("api/agent/v1/inventory", inventory, ct))
            {
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    response.EnsureSuccessStatusCode();

                    return;
                }

                delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? DefaultRetryDelay;
            }

            delay = TimeSpan.FromSeconds(Math.Clamp(
                delay.TotalSeconds,
                MinimumRetrySeconds,
                MaximumRetrySeconds) + Random.Shared.NextDouble() * RetryJitterSeconds);
            await Task.Delay(delay, ct);
        }
    }
}