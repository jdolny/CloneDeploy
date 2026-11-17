namespace CloneDeploy_Agent.MacOS;

/// <summary>
/// Wakes the deployment worker when a check-in or server request calls for work.
/// </summary>
public sealed class MacTaskSignal
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int regular;

    public void Request(bool regularCheckin)
    {
        if (regularCheckin)
            Interlocked.Exchange(ref regular, 1);

        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        await signal.WaitAsync(ct);

        return Interlocked.Exchange(ref regular, 0) != 0;
    }
}

public sealed class MacTaskWorker(MacTaskRunner runner, MacTaskSignal signal)
{
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var regular = await signal.WaitAsync(ct);
                var retry = true;
                try { retry = await runner.RunAsync(regular, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Console.Error.WriteLine($"Device tasks will retry ({ex.GetType().Name}): {ex.Message}"); }
                if (retry)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(15, 46)), ct);
                    signal.Request(regular);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
