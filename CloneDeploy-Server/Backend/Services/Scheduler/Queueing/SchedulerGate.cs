namespace CloneDeploy_Server.Backend.Services.Scheduler;

// Shared across service scopes to serialize scheduler writes within this server process.
/// <summary>
/// Keeps scheduling changes from overlapping within the running server.
/// </summary>
public sealed class SchedulerGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
