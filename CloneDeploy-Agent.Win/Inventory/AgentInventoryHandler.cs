using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Handles server requests to collect inventory and reports whether it was saved successfully.
/// </summary>
public sealed class AgentInventoryHandler(
    AgentOptions options,
    AgentIdentity identity,
    InventoryCollector collector,
    IHostApplicationLifetime lifetime,
    ILogger<AgentInventoryHandler> logger,
    AgentActivityTracker activityTracker)
{
    private const int MaximumRememberedRequests = 128;

    private static readonly TimeSpan CollectionTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SubmissionTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, AgentInventoryReceipt> completed = new();

    public Task<AgentInventoryReceipt> CollectAsync(AgentInventoryRequest request, Guid deviceGuid) =>
        CollectAsync(request, deviceGuid, null, default);

    public async Task<AgentInventoryReceipt> CollectAsync(
        AgentInventoryRequest request,
        Guid deviceGuid,
        Func<AgentInventory, CancellationToken, Task>? collectCustom,
        CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.DeviceGuid != deviceGuid
            || identity.State.DeviceGuid != deviceGuid
            || request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new(request.RequestId, false, "The inventory request is invalid or expired.");

        if (!await gate.WaitAsync(0))
            return new(request.RequestId, false, "Inventory is already running.");

        try
        {
            if (completed.TryGetValue(request.RequestId, out var previous))
                return previous;

            using var activity = activityTracker.Begin(AgentActivityKind.CollectingInventory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping, ct);
            timeout.CancelAfter(CollectionTimeout);

            using var certificate = identity.GetClientCertificate()
                                    ?? throw new InvalidOperationException("Missing agent certificate.");

            logger.LogInformation($"Inventory {request.RequestId}: collecting device inventory");
            var inventory = await Task.Run(() => collector.Collect(timeout.Token), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();

            if (inventory.ComputerSystem is null || inventory.Os is null)
                return new(
                    request.RequestId,
                    false,
                    "Could not collect computer system or operating system inventory. Check the agent log.");

            if (collectCustom is not null)
            {
                // Each script has its own execution timeout; the native scan limit must not cut it short.
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await collectCustom(inventory, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                timeout.CancelAfter(CollectionTimeout);
            }

            using var handler = new HttpClientHandler();
            AgentServerTrust.Configure(handler, options);
            handler.ClientCertificates.Add(certificate);

            using var client = new HttpClient(new AgentResponseVerification(handler, options))
            {
                BaseAddress = options.GetServerUri(),
                Timeout = SubmissionTimeout
            };

            logger.LogInformation($"Inventory {request.RequestId}: collection complete; submitting to the server");
            await AgentInventorySubmission.SendAsync(client, inventory, timeout.Token);

            var detail = inventory.Warnings.Count == 0
                ? "Inventory saved."
                : $"Inventory saved. Unavailable sections: {string.Join(", ", inventory.Warnings)}";
            var receipt = new AgentInventoryReceipt(request.RequestId, true, detail);

            if (completed.Count >= MaximumRememberedRequests)
                completed.Remove(completed.Keys.First());

            completed.Add(request.RequestId, receipt);

            logger.LogInformation($"Inventory {request.RequestId}: {detail}");

            return receipt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Inventory request {request.RequestId} failed");

            return new(
                request.RequestId,
                false,
                "Inventory did not complete. Check the agent log and server connection.");
        }
        finally
        {
            gate.Release();
        }
    }
}
