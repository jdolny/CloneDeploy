namespace CloneDeploy_Agent;

/// <summary>
/// Wakes the deployment worker when a check-in or server request calls for work.
/// </summary>
public sealed class AgentTaskSignal
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

/// <summary>
/// Processes deployment requests in the background and schedules another attempt when needed.
/// </summary>
public sealed class AgentTaskWorker(
    AgentTaskRunner runner,
    AgentTaskSignal signal,
    ILogger<AgentTaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var regular = await signal.WaitAsync(stoppingToken);
            var retry = true;

            try
            {
                retry = await runner.RunAsync(regular, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Task processing will retry ({ex.GetType().Name})");
            }

            if (retry)
            {
                var seconds = Random.Shared.Next(TaskRetry.MinimumSeconds, TaskRetry.MaximumSeconds + 1);
                logger.LogInformation($"Retrying device task processing in {seconds}s");
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
                signal.Request(regular);
            }
        }
    }
}