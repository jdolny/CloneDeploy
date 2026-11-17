using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed class MacInventoryHandler(MacConfiguration configuration, MacIdentity identity, string directory,
    Func<CancellationToken, Task<AgentInventory>> collect, CancellationToken stopping = default)
{
    private const int MaximumRememberedRequests = 128;

    public MacActivityTracker? Activity { get; init; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, AgentInventoryReceipt> completed = [];
    public Task<AgentInventoryReceipt> CollectAsync(AgentInventoryRequest request, Guid device, CancellationToken ct = default) => CollectAsync(request, device, null, ct);
    public async Task<AgentInventoryReceipt> CollectAsync(AgentInventoryRequest request, Guid device,
        Func<AgentInventory, CancellationToken, Task>? enrich, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.DeviceGuid != device || identity.State.DeviceGuid != device ||
            request.ExpiresAtUtc <= DateTimeOffset.UtcNow || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(10))
            return new(request.RequestId, false, "Inventory request is invalid or expired.");
        if (!await gate.WaitAsync(0, ct)) return new(request.RequestId, false, "Inventory is already running.");
        try
        {
            if (completed.TryGetValue(request.RequestId, out var prior)) return prior;
            using var activity = Activity?.Begin(AgentActivityKind.CollectingInventory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping, ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var inventory = await collect(timeout.Token);
            if (inventory.ComputerSystem is null || inventory.Os is null)
                return new(request.RequestId, false, "Required computer or operating system inventory is unavailable.");
            if (enrich is not null)
            {
                // Custom scripts enforce their configured timeouts independently of native inventory probes.
                timeout.Token.ThrowIfCancellationRequested();
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                await enrich(inventory, timeout.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
            }
            using var certificate = identity.GetClientCertificate() ?? throw new IOException("Agent certificate unavailable.");
            using var handler = new HttpClientHandler();
            var options = configuration.Options(directory);
            AgentServerTrust.Configure(handler, options); handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(new AgentResponseVerification(handler, options)) { BaseAddress = options.GetServerUri(), Timeout = TimeSpan.FromSeconds(45) };
            await AgentInventorySubmission.SendAsync(client, inventory, timeout.Token);
            var detail = inventory.Warnings.Count == 0 ? "Inventory saved." : "Inventory saved; unavailable sections: " + string.Join(", ", inventory.Warnings);
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} {detail}");
            var receipt = new AgentInventoryReceipt(request.RequestId, true, detail);
            if (completed.Count >= MaximumRememberedRequests) completed.Remove(completed.Keys.First());
            completed.Add(request.RequestId, receipt);
            return receipt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || stopping.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} Inventory failed ({ex.GetType().Name}): {ex.Message}");
            return new(request.RequestId, false, "Inventory did not complete. Check the agent log.");
        }
        finally { gate.Release(); }
    }
}
