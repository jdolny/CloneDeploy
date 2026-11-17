using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace CloneDeploy_Agent;

/// <summary>
/// Keeps a live connection to the server so the agent can receive requests between check-ins.
/// </summary>
public sealed class AgentRealtimeClient(
    AgentOptions options,
    AgentIdentity identity,
    ILogger<AgentRealtimeClient> logger,
    AgentInventoryHandler inventory,
    AgentActivityTracker activityTracker) : IAsyncDisposable
{
    private static readonly TimeSpan ActivityReportTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectionMaintenanceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public AgentHelpPresence HelpPresence { get; } = new(TimeProvider.System);

    public AgentImagingBoot? ImagingBoot { get; set; }

    public AgentFileLoggerProvider? FileLog { get; set; }

    public AgentTaskSignal? TaskSignal { get; set; }

    private readonly SemaphoreSlim activitySend = new(1, 1);
    private string? activityConnection;
    private long activitySequence = -1;

    public AgentCheckinSignal CheckinSignal { get; } = new();

    private HubConnection? connection;
    private readonly AgentDeviceTools deviceTools = new();
    private readonly AgentWakeRelay wakeRelay = new();
    private readonly AgentRemoteDesktop remoteDesktop = new(options, identity, logger);
    public AgentRemoteConsent RemoteConsent => remoteDesktop.Consent;
    private readonly AgentTerminal terminal = new(options, identity, logger);
    private X509Certificate2? certificate;
    private string? certificatePem;

    // Worker calls this sequentially; only Worker changes the identity or connection lifecycle.
    public async Task MaintainAsync(CancellationToken ct)
    {
        if (identity.State.CertificatePem != certificatePem)
            await DisconnectAsync();

        if (identity.State.CertificatePem is null)
            return;

        if (connection is null)
        {
            certificate = identity.GetClientCertificate()
                          ?? throw new InvalidDataException("Missing agent certificate.");
            certificatePem = identity.State.CertificatePem;
            var deviceGuid = identity.State.DeviceGuid!.Value;
            var hubUrl = new Uri(options.GetServerUri(), AgentHubProtocol.Route.TrimStart('/'));
            connection = new HubConnectionBuilder()
                .WithUrl(
                    hubUrl,
                    transport =>
                    {
                        transport.Transports = HttpTransportType.WebSockets;
                        (transport.ClientCertificates ??= new X509CertificateCollection()).Add(certificate);
                        transport.HttpMessageHandlerFactory = handler =>
                        {
                            if (handler is not HttpClientHandler http)
                                throw new InvalidOperationException("Unsupported SignalR HTTP handler.");

                            AgentServerTrust.Configure(http, options);

                            return new AgentResponseVerification(http, options);
                        };
                        transport.WebSocketFactory = async (context, token) => await AgentResponseVerification.ConnectSocketAsync(context.Uri, options, certificate, token);
                    })
                .Build();
            connection.On<AgentImagingCommand, AgentImagingReply>(
                AgentHubProtocol.ImagingBoot,
                request => ImagingBoot?.ReceiveAsync(request, deviceGuid)
                           ?? Task.FromResult(new AgentImagingReply(
                               request.RequestId,
                               false,
                               "Agent imaging is unavailable.")));
            connection.On<AgentLogRequest, AgentLogReply>(
                AgentHubProtocol.ReadLog,
                request => FileLog?.Read(request, deviceGuid)
                           ?? new AgentLogReply(
                               request.RequestId,
                               false,
                               "",
                               "File logging is unavailable on this agent."));
            connection.On<AgentDeviceToolRequest, AgentDeviceToolReply>(
                AgentHubProtocol.DeviceTools,
                request => deviceTools.ExecuteAsync(request, deviceGuid));
            connection.On<AgentWakeRequest, AgentWakeReply>(AgentHubProtocol.Wake,
                request => wakeRelay.ExecuteAsync(request, deviceGuid));
            connection.On<AgentRemoteDesktopRequest, AgentRemoteDesktopReply>(
                AgentHubProtocol.RemoteDesktop,
                request => remoteDesktop.StartAsync(request, deviceGuid));
            connection.On<string[]>(AgentHubProtocol.HelpChanged, sids => HelpPresence.Changed(sids));
            connection.On<Guid>(AgentHubProtocol.RemoteDesktopStop, id => remoteDesktop.Cancel(id));
            connection.On<AgentTerminalRequest, AgentTerminalReply>(AgentHubProtocol.Terminal,
                request => terminal.Start(request, deviceGuid));
            connection.On<AgentDesktopSessionsRequest, AgentDesktopSession[]>(
                AgentHubProtocol.DesktopSessions,
                request =>
                {
                    if (request.DeviceGuid != deviceGuid || request.ExpiresAtUtc < DateTimeOffset.UtcNow
                                                         ||
                                                         request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                        return [];

                    return CloneDeploy_Agent.RemoteDesktop.DesktopSessions.List()
                        .Select(x => new AgentDesktopSession(
                            x.Id,
                            x.UserName,
                            x.Connection,
                            x.State,
                            x.Available) { ConsentProtocol = 1 }).ToArray();
                });
            connection.On<AgentCheckinCommand, AgentCheckinReceipt>(AgentHubProtocol.CheckInNow,
                request => CheckinSignal.Receive(request, deviceGuid));
            connection.On(
                AgentHubProtocol.TasksAvailable,
                () =>
                {
                    logger.LogInformation("Server requested a device task check");
                    TaskSignal?.Request(false);
                });
            connection.On<AgentInventoryRequest, AgentInventoryReceipt>(
                AgentHubProtocol.CollectInventory,
                request => inventory.CollectAsync(request, deviceGuid));
        }

        if (connection.State == HubConnectionState.Disconnected)
        {
            logger.LogDebug("Connecting to the server to receive immediate requests");
            await connection.StartAsync(ct);
            HelpPresence.Changed([]);
            logger.LogInformation("Connected to server; listening for check-in, task, inventory, log and remote desktop requests");
        }

        // New servers renew Help presence as part of the existing device heartbeat.
        // A missing/failed optional method falls back to the original device heartbeat.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var cache = new AgentFeatureCache(options);
            var previous = cache.Read();
            var features = await connection.InvokeAsync<AgentFeatures?>(AgentHubProtocol.HelpPresence,
                previous.HelpDeskEnabled ? HelpPresence.Current() : [], timeout.Token) ?? new();
            cache.Save(features);
            if (previous.HelpDeskEnabled != features.HelpDeskEnabled) HelpPresence.Changed([]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Help heartbeat unavailable; using device heartbeat.");
            await connection.InvokeAsync(AgentHubProtocol.Heartbeat, ct);
        }
        await SendActivityAsync(ct);
    }

    public async Task SendActivityAsync(CancellationToken ct)
    {
        await activitySend.WaitAsync(ct);

        try
        {
            var live = connection;
            var snapshot = activityTracker.Current;

            if (live?.State != HubConnectionState.Connected)
                return;

            var connectionId = live.ConnectionId;

            if (activityConnection == connectionId && activitySequence >= snapshot.Sequence)
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ActivityReportTimeout);
            await live.InvokeAsync(AgentHubProtocol.ReportActivity, snapshot, timeout.Token);
            activityConnection = connectionId;
            activitySequence = snapshot.Sequence;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Telemetry must never stop execution. Retry the latest snapshot at the existing heartbeat.
            logger.LogDebug(exception, "Activity report deferred until the next live heartbeat.");
        }
        finally
        {
            activitySend.Release();
        }
    }

    public async Task WaitForCheckInAsync(TimeSpan delay, CancellationToken ct)
    {
        var nextCheckIn = DateTimeOffset.UtcNow + delay;
        do
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectionMaintenanceTimeout);
                await MaintainAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var reason = exception is OperationCanceledException ? "Connection timed out" : exception.Message;
                logger.LogWarning($"Live connection unavailable; retrying shortly. {reason}");
                logger.LogDebug(exception, "Live connection failure details.");
                await DisconnectAsync();
            }

            var remaining = nextCheckIn - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < HeartbeatInterval ? remaining : HeartbeatInterval, ct);
        } while (!ct.IsCancellationRequested);
    }

    public async Task WaitForNextCheckInAsync(TimeSpan delay, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var scheduled = WaitForCheckInAsync(delay, wait.Token);
        var requested = CheckinSignal.WaitAsync(wait.Token);

        try
        {
            await await Task.WhenAny(scheduled, requested);
        }
        finally
        {
            // Finish both waits before the worker touches the connection or starts another cycle.
            await wait.CancelAsync();

            try
            {
                await Task.WhenAll(scheduled, requested);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }

        await remoteDesktop.DisposeAsync();
        await terminal.DisposeAsync();
        certificate?.Dispose();
        certificate = null;
        certificatePem = null;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
