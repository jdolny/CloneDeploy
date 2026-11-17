using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Deployment;

/// <summary>
/// Coordinates deployment admission, lease heartbeats, machine operations, progress, and result reporting.
/// </summary>
internal sealed class DeploymentRunner(ImagingClient client, IDeploymentMachine machine, DiagnosticLog log)
{
    public CloneDeploy_Agent.Contracts.DeploymentCompletionAction? CompletedAction { get; private set; }

    public async IAsyncEnumerable<DeploymentProgress> RunAsync(Guid deploymentId, [EnumeratorCancellation] CancellationToken ct)
    {
        CompletedAction = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var updates = Channel.CreateUnbounded<DeploymentProgress>(new UnboundedChannelOptions { SingleReader = true });
        DeploymentProgress current = new(DeploymentStage.Preparing, null) { Status = "Checking deployment assignment and imaging slot" };
        var rate = NetworkTransferRate.ForReceive();
        rate.Sample();
        var worker = RunCore();
        try { await foreach (var update in updates.Reader.ReadAllAsync(ct)) yield return update; }
        finally { stop.Cancel(); await worker; }

        async Task RunCore()
        {
            bool started = false;
            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            Task? heartbeat = null;
            try
            {
                updates.Writer.TryWrite(current);
                await client.RenewDeploymentAsync(deploymentId, stop.Token);
                heartbeat = Heartbeat();
                while (true)
                {
                    var queue = await client.EnterDeploymentQueueAsync(stop.Token);
                    if (queue.Admitted) break;
                    var waiting = new DeploymentProgress(DeploymentStage.Preparing, null)
                        { Status = $"Waiting for an imaging slot — position {queue.Position} ({queue.Active}/{queue.Limit} active)" };
                    Volatile.Write(ref current, waiting);
                    updates.Writer.TryWrite(waiting);
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                }
                started = true;
                Volatile.Write(ref current, new(DeploymentStage.Preparing, null) { Status = "Loading assigned image details" });
                updates.Writer.TryWrite(current);
                var image = await client.GetDeploymentExecutionAsync(deploymentId, stop.Token);
                if (image.Multicast is not null)
                {
                    Volatile.Write(ref current, new(DeploymentStage.Preparing, null) { Status = "Preparing multicast. Once connected, press Enter to start without missing receivers." });
                    updates.Writer.TryWrite(current);
                }
                await machine.PrepareAsync(image, (stream, cancel) => client.CopyDeploymentImageAsync(image, stream, cancel), update => { Volatile.Write(ref current, update); updates.Writer.TryWrite(update); }, stop.Token);
                await machine.ApplyAsync(image, (stream, cancel) => client.CopyDeploymentImageAsync(image, stream, cancel),
                    update => {
                        if (image.Multicast is not null && update.Stage == DeploymentStage.Deploying && update.Percent is null)
                            update = update with { Status = "Multicast: waiting for all receivers. Once connected, press Enter to start early." };
                        Volatile.Write(ref current, update); updates.Writer.TryWrite(update);
                    }, stop.Token);
                heartbeatStop.Cancel();
                await heartbeat;
                stop.Token.ThrowIfCancellationRequested();
                var finishing = new DeploymentProgress(DeploymentStage.Finalizing, null) { Status = "Reporting deployment result to server" };
                Volatile.Write(ref current, finishing);
                updates.Writer.TryWrite(finishing);
                await client.FinishDeploymentAsync(true, machine.AgentInstalled, stop.Token);
                CompletedAction = image.AfterDeployment;
                updates.Writer.TryWrite(new(DeploymentStage.Finalizing, 100, true));
                updates.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                log.Failure("Deployment result", ex);
                if (started)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await client.FinishDeploymentAsync(false, timeout.Token); }
                    catch (Exception report) { log.Failure("Deployment failure report", report); }
                }
                updates.Writer.TryComplete(ex);
            }
            finally
            {
                heartbeatStop.Cancel();
                if (heartbeat is not null) await heartbeat;
            }
            async Task Heartbeat()
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                    while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                    {
                        await client.RenewDeploymentAsync(deploymentId, heartbeatStop.Token);
                        await client.ReportDeploymentProgressAsync(Volatile.Read(ref current) with { NetworkBytesPerSecond = rate.Sample() }, heartbeatStop.Token);
                    }
                }
                catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
                catch (Exception ex) { log.Failure("Deployment lease renewal failed; stopping deployment", ex); stop.Cancel(); }
            }
        }
    }
}
