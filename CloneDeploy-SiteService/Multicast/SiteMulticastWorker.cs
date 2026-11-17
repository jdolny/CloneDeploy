using System.Net;
using System.Net.Http.Json;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

public sealed class SiteMulticastWorker(SiteStorageCache cache, ILogger<SiteMulticastWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreparationPollInterval = TimeSpan.FromSeconds(5);
    private const int MaximumOpenRetries = 12;

    private SiteLocalState? desired;
    public void Configure(SiteLocalState? state) => Interlocked.Exchange(ref desired, state);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                running.RemoveAll(t => t.IsCompleted);
                try
                {
                    var state = Volatile.Read(ref desired);
                    if (state?.Configuration is not null && running.Count < 8)
                    {
                        using var http = SiteEnrollmentClient.CreateClient(state, true); http.Timeout = RequestTimeout;
                        using var response = await http.PostAsync("/api/site-services/multicast/claim", null, ct);
                        response.EnsureSuccessStatusCode();
                        if (response.StatusCode != HttpStatusCode.NoContent)
                            running.Add(Run(state, (await response.Content.ReadFromJsonAsync<MulticastOffer>(ct))!, ct));
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Multicast sender polling failed"); }
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(running); }
    }
    private async Task Run(SiteLocalState state, MulticastOffer job, CancellationToken ct)
    {
        using var http = SiteEnrollmentClient.CreateClient(state, true); http.Timeout = RequestTimeout;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var keepAlive = KeepAlive();
        try
        {
            await using var input = await OpenInput();
            preparing.Cancel(); await keepAlive;
            await UdpcastSender.RunAsync(job, input, Report, stop.Token);
            await Report(new(true, true, null), stop.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Site multicast {Session} stopped", job.Id);
            using var timeout = new CancellationTokenSource(CompletionTimeout);
            try { await Report(new(false, false, ex.Message), timeout.Token); } catch (Exception) { }
        }
        finally { preparing.Cancel(); await keepAlive; }
        async Task<Stream> OpenInput()
        {
            // The storage worker may still be loading its first manifest after a service restart.
            for (var attempt = 0; ; attempt++)
            {
                try { return await cache.OpenReadAsync(state.Configuration!.RegistrationId, job.File, stop.Token); }
                catch (IOException) when (attempt < MaximumOpenRetries) { await Task.Delay(PreparationPollInterval, stop.Token); }
            }
        }
        async Task<MulticastControl> Report(MulticastProgress progress, CancellationToken cancel)
        {
            if (Volatile.Read(ref desired)?.Configuration?.RegistrationId != state.Configuration!.RegistrationId)
                throw new OperationCanceledException("Site service configuration withdrawn.");
            using var response = await http.PostAsJsonAsync($"/api/site-services/multicast/{job.Id}/{job.Lease}/report", progress, cancel);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MulticastControl>(cancel))!;
        }
        async Task KeepAlive()
        {
            try
            {
                while (!preparing.IsCancellationRequested)
                { await Report(new(false, false, null), preparing.Token); await Task.Delay(PreparationPollInterval, preparing.Token); }
            }
            catch (OperationCanceledException) when (preparing.IsCancellationRequested) { }
            catch { stop.Cancel(); }
        }
    }
}
