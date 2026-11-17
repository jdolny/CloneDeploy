using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Device connection with optional, session-validated portal Help subscriptions.</summary>
public sealed class MacRealtimeClient(MacConfiguration configuration, MacIdentity identity, string directory) : IAsyncDisposable
{
    public bool IsConnected => connection?.State == HubConnectionState.Connected;
    public MacActivityTracker? Activity { get; init; }
    public MacTaskSignal? Tasks { get; init; }
    private readonly SemaphoreSlim activitySend = new(1, 1);
    private string? lastActivityConnection;
    private long lastActivitySequence = -1;
    public MacRemoteDesktop? Remote { get; init; }
    public MacHelpPresence? HelpPresence { get; init; }
    public MacDeviceTools DeviceTools { get; init; } = new();
    private readonly AgentWakeRelay wake = new();
    public MacInventoryHandler? Inventory { get; init; }
    private HubConnection? connection;
    private X509Certificate2? certificate;
    private string? certificatePem;
    private readonly MacAgentLogReader logs = new();
    private MacTerminal? terminal;
    public AgentCheckinSignal CheckinSignal { get; } = new();
    private const string Unsupported = "This action is not available in the macOS agent yet.";

    // Called by one maintenance loop, independently of slow check-in/update downloads.
    public async Task MaintainAsync(CancellationToken ct)
    {
        if (certificatePem != identity.State.CertificatePem) await DisconnectAsync();
        if (identity.State.CertificatePem is null) return;
        if (connection is null)
        {
            var options = configuration.Options(directory);
            certificate = identity.GetClientCertificate() ?? throw new InvalidDataException("Missing agent certificate.");
            certificatePem = identity.State.CertificatePem;
            var device = identity.State.DeviceGuid!.Value;
            connection = new HubConnectionBuilder().WithUrl(new Uri(options.GetServerUri(), AgentHubProtocol.Route.TrimStart('/')), transport =>
            {
                transport.Transports = HttpTransportType.WebSockets;
                (transport.ClientCertificates ??= new X509CertificateCollection()).Add(certificate);
                transport.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is not HttpClientHandler http) throw new InvalidOperationException("Unsupported HTTP handler.");
                    AgentServerTrust.Configure(http, options);
                    return new AgentResponseVerification(http, options);
                };
                transport.WebSocketFactory = async (context, token) => await AgentResponseVerification.ConnectSocketAsync(context.Uri, options, certificate, token);
            }).Build();
            var connectionTerminal = terminal = new MacTerminal(options, identity);
            connection.Closed += async _ => { await connectionTerminal.StopAsync(); if (Remote is not null) await Remote.StopAsync(); };
            connection.On<AgentCheckinCommand, AgentCheckinReceipt>(AgentHubProtocol.CheckInNow, request =>
                request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ? new(request.RequestId, false) : CheckinSignal.Receive(request, device));
            connection.On<AgentInventoryRequest, AgentInventoryReceipt>(AgentHubProtocol.CollectInventory, r => Inventory?.CollectAsync(r, device) ?? Task.FromResult(new AgentInventoryReceipt(r.RequestId, false, Unsupported)));
            connection.On<AgentDeviceToolRequest, AgentDeviceToolReply>(AgentHubProtocol.DeviceTools, r => DeviceTools.ExecuteAsync(r, device));
            connection.On<AgentLogRequest, AgentLogReply>(AgentHubProtocol.ReadLog, r => logs.Read(r, device));
            connection.On<AgentTerminalRequest, AgentTerminalReply>(AgentHubProtocol.Terminal, r => connectionTerminal.Start(r, device));
            connection.On<AgentRemoteDesktopRequest, AgentRemoteDesktopReply>(AgentHubProtocol.RemoteDesktop, r => Remote?.StartAsync(r, device) ?? Task.FromResult(new AgentRemoteDesktopReply(r.SessionId, false, Unsupported)));
            connection.On<Guid>(AgentHubProtocol.RemoteDesktopStop, id => Remote?.Cancel(id));
            connection.On<AgentDesktopSessionsRequest, AgentDesktopSession[]>(AgentHubProtocol.DesktopSessions, r => Remote?.List(r, device) ?? []);
            connection.On<AgentImagingCommand, AgentImagingReply>(AgentHubProtocol.ImagingBoot, r => new(r.RequestId, false, Unsupported));
            connection.On<AgentWakeRequest, AgentWakeReply>(AgentHubProtocol.Wake, r => wake.ExecuteAsync(r, device));
            connection.On<string[]>(AgentHubProtocol.HelpChanged, _ => HelpPresence?.Changed());
            connection.On(AgentHubProtocol.TasksAvailable, () => Tasks?.Request(false));
        }
        if (connection.State == HubConnectionState.Disconnected)
        {
            await connection.StartAsync(ct);
            Tasks?.Request(false);
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} Connected to server; listening for check-in requests.");
        }
        await connection.InvokeAsync(AgentHubProtocol.Heartbeat, ct);
        await SendActivityAsync(ct);
        if (HelpPresence is not null)
        {
            try { await connection.InvokeAsync("PortalHelpPresence", HelpPresence.Sessions(), ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { Console.Error.WriteLine($"Help subscription unavailable ({ex.GetType().Name}); device connection continues."); }
        }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await MaintainAsync(timeout.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} Live connection unavailable ({ex.GetType().Name}); retrying.");
                    await DisconnectAsync();
                }
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task RunActivityAsync(CancellationToken ct)
    {
        if (Activity is null) return;
        try
        {
            while (true)
            {
                await Activity.WaitAsync(ct);
                await SendActivityAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SendActivityAsync(CancellationToken ct)
    {
        await activitySend.WaitAsync(ct);
        try
        {
            var snapshot = Activity?.Current;
            var live = connection;
            if (snapshot is null || live?.State != HubConnectionState.Connected) return;
            var connectionId = live.ConnectionId;
            if (lastActivityConnection == connectionId && lastActivitySequence >= snapshot.Sequence) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await live.InvokeAsync(AgentHubProtocol.ReportActivity, snapshot, timeout.Token);
            lastActivityConnection = connectionId;
            lastActivitySequence = snapshot.Sequence;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { Console.Error.WriteLine($"Activity reporting will retry ({ex.GetType().Name})."); }
        finally { activitySend.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (Remote is not null) await Remote.StopAsync();
        if (terminal is not null) await terminal.DisposeAsync();
        terminal = null;
        if (connection is not null) await connection.DisposeAsync();
        connection = null; certificate?.Dispose(); certificate = null; certificatePem = null;
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
