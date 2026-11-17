using System.Net;
using System.Net.Sockets;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Owns the UDP listeners and reloads persisted settings without restarting the web host.</summary>
public sealed class PxeWorker(IServiceScopeFactory scopes, ILogger<PxeWorker> logger, NativePxeReservations? reservations = null) : BackgroundService
{
    private PxeStatusDto _status = new("Starting", "Waiting for settings.");
    public PxeStatusDto Status => Volatile.Read(ref _status);
    public BootActivityBuffer Activity { get; } = new();
    private void SetStatus(string state, string detail)
    {
        var previous = Status;
        Volatile.Write(ref _status, new(state, detail));
        if (previous.State != state || state != "Running" && previous.Detail != detail)
            Activity.Add(state == "Error" ? "Error" : "Information", $"{state}: {detail}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PxeSettingsDto? active = null;
        CancellationTokenSource? listenerStop = null;
        var sockets = new List<UdpClient>();
        var listeners = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    PxeSettingsDto settings;
                    try
                    {
                        settings = await scope.ServiceProvider.GetRequiredService<PxeSettingsStore>().ReadAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && active?.Enabled == true &&
                                               listeners.Count > 0 && listeners.All(x => !x.IsCompleted))
                    {
                        // A configuration-store outage must not interrupt healthy PXE listeners.
                        SetStatus("Running", "Using the last loaded settings; settings refresh failed. See server logs.");
                        logger.LogWarning(ex, "PXE settings refresh failed; keeping the active listeners");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                    if (settings != active || listeners.Any(x => x.IsCompleted))
                    {
                        foreach (var listener in listeners.Where(x => x.IsFaulted)) await listener;
                        await StopListeners();
                        active = null;
                        if (!settings.Enabled) SetStatus("Disabled", "ProxyDHCP is disabled.");
                        else
                        {
                            if (settings.Validate() is { } error) throw new InvalidOperationException(error);
                            var configuration = new PxePacketConfiguration(settings) { Reservations = reservations };
                            listenerStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            foreach (var port in new[] { 67, 4011 })
                            {
                                var socket = new UdpClient(AddressFamily.InterNetwork);
                                sockets.Add(socket);
                                socket.ExclusiveAddressUse = true;
                                socket.EnableBroadcast = true;
                                socket.Client.Bind(new IPEndPoint(IPAddress.Parse(settings.Interface), port));
                                listeners.Add(ListenPreparedAsync(socket, port, configuration, listenerStop.Token));
                            }
                            SetStatus("Running", $"Listening on {settings.Interface}; UDP {string.Join(", ", sockets.Select(x => ((IPEndPoint)x.Client.LocalEndPoint!).Port))}. {reservations?.Count ?? 0} native reservations. {reservations?.Status}");
                        }
                        active = settings;
                    }
                    if (active.Enabled)
                        SetStatus("Running", $"Listening on {active.Interface}; UDP {string.Join(", ", sockets.Select(x => ((IPEndPoint)x.Client.LocalEndPoint!).Port))}. {reservations?.Count ?? 0} native reservations. {reservations?.Status}");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await StopListeners();
                    active = null;
                    SetStatus("Error", ex is SocketException or InvalidOperationException ? ex.Message : "Unable to load PXE settings. See server logs.");
                    logger.LogError(ex, "PXE service failed; retrying in five seconds");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await StopListeners();
            SetStatus("Stopped", "Application is stopping.");
        }

        async Task StopListeners()
        {
            if (listenerStop is not null) await listenerStop.CancelAsync();
            foreach (var socket in sockets) socket.Dispose();
            try { await Task.WhenAll(listeners); }
            catch (Exception ex) { logger.LogDebug(ex, "PXE listeners stopped"); }
            sockets.Clear();
            listeners.Clear();
            listenerStop?.Dispose();
            listenerStop = null;
        }
    }

    private async Task ListenPreparedAsync(UdpClient socket, int port, PxePacketConfiguration configuration, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await socket.ReceiveAsync(ct); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            var reply = PxePacketHandler.CreateReply(packet.Buffer, packet.RemoteEndPoint, port, configuration);
            if (reply is null) continue;
            // BOOTP chaddr contains the client MAC, including requests received through a relay.
            var mac = packet.Buffer[1] == 1 && packet.Buffer[2] == 6
                ? BitConverter.ToString(packet.Buffer, 28, 6).Replace('-', ':')
                : "unknown";
            Activity.Add("Information", $"Request from {packet.RemoteEndPoint} (MAC {mac}) on UDP {port}; replying to {reply.Destination}");
            logger.LogDebug("PXE request from {Source} (MAC {MacAddress}) on UDP {Port}; replying to {Destination}; architecture {Architecture}; bootloader {BootFile}; reason: {Reason}", packet.RemoteEndPoint, mac, port, reply.Destination, reply.Architecture, reply.BootFile ?? "(none)", reply.Reason);
            try { await socket.SendAsync(reply.Data, reply.Destination, ct); }
            catch (SocketException ex) { Activity.Add("Warning", $"Could not send PXE reply to {reply.Destination} (MAC {mac}): {ex.Message}"); logger.LogDebug(ex, "Could not send PXE reply to {Destination} (MAC {MacAddress})", reply.Destination, mac); }
        }
    }
}
