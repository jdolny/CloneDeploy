namespace CloneDeploy.SiteService;

public sealed class SiteStorageWorker(SiteStorageCache cache, ILogger<SiteStorageWorker> logger) : BackgroundService
{
    private const int MaximumManifestBytes = 32 * 1024 * 1024;
    private const int MaximumErrorCharacters = 950;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly object sync = new();
    private SiteLocalState? desired;
    private CancellationTokenSource? cycle;
    private string? error;
    public string? Error => Volatile.Read(ref error);
    public void Configure(SiteLocalState? state)
    {
        lock (sync)
        {
            if (desired?.Configuration?.Revision != state?.Configuration?.Revision || (desired is null) != (state is null)) cycle?.Cancel();
            desired = state;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SiteLocalState? state; CancellationTokenSource iteration;
                lock (sync) { state = desired; cycle = iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); }
                try
                {
                    if (state?.Configuration is { } configuration)
                    {
                        using var http = SiteEnrollmentClient.CreateClient(state, true);
                        http.Timeout = RequestTimeout; http.MaxResponseContentBufferSize = MaximumManifestBytes;
                        await cache.SynchronizeAsync(http, configuration.RegistrationId, iteration.Token);
                        Volatile.Write(ref error, null);
                    }
                }
                catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Volatile.Write(ref error, "Storage replication: " + ex.Message[..Math.Min(MaximumErrorCharacters, ex.Message.Length)]);
                    logger.LogWarning(ex, "Site storage synchronization will retry");
                }
                finally { lock (sync) { cycle = null; iteration.Dispose(); } }
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
