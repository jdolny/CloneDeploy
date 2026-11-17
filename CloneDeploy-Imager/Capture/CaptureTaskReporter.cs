using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Imaging;

// A heartbeat runs even when capture is scanning, a chunk is blocked on the network,
// or the server is hashing the uploaded WIM. Only the latest progress is sent.
/// <summary>
/// Sends the latest capture progress on a heartbeat and stops capture if server reporting fails.
/// </summary>
internal sealed class CaptureTaskReporter : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task worker;
    private DeploymentProgress current = new(DeploymentStage.Preparing, null);
    public CaptureTaskReporter(ImagingClient client, Guid id, DiagnosticLog log, CancellationToken ct, CancellationTokenSource? captureStop = null)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        worker = Run(client, id, log, captureStop);
    }
    public DeploymentProgress Update(DeploymentProgress progress)
    {
        Volatile.Write(ref current, progress);
        return progress;
    }
    private async Task Run(ImagingClient client, Guid id, DiagnosticLog log, CancellationTokenSource? captureStop)
    {
        var failed = false;
        var rate = new NetworkTransferRate();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                try { await client.ReportCaptureAsync(id, Volatile.Read(ref current) with { NetworkBytesPerSecond = rate.Sample() }, stop.Token); failed = false; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) when (captureStop is not null && ex is InvalidOperationException or SessionExpiredException)
                {
                    log.Failure("Imaging task rejected by server; stopping capture", ex);
                    captureStop.Cancel();
                    return;
                }
                catch (Exception ex)
                {
                    if (!failed) log.Failure("Imaging task progress update; will retry", ex);
                    failed = true;
                }
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await worker;
        stop.Dispose();
    }
}
