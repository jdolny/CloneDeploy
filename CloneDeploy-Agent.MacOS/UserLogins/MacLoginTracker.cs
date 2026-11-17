namespace CloneDeploy_Agent.MacOS;

public sealed class MacLoginTracker(UserLoginJournal journal)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Func<CancellationToken, Task<string[]>> Users { get; init; } = MacSystemStatus.UsersAsync;
    public async Task<bool> SampleAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { journal.Observe(await Users(ct), DateTimeOffset.UtcNow); return true; }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { Console.Error.WriteLine($"Login history probe failed ({ex.GetType().Name}); previous observations retained."); return false; }
        finally { gate.Release(); }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try { while (true) { await SampleAsync(ct); await Task.Delay(TimeSpan.FromSeconds(10), ct); } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
