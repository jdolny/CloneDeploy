using System.Net;
using System.Net.Sockets;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Services;
using CodePlex.JPMikkers.TFTP;

namespace CloneDeploy.SiteService;

public sealed class SiteBootWorker(SiteBootCache cache, ILogger<SiteBootWorker> logger) : BackgroundService
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private const int TftpPort = 69;
    private const int DhcpServerPort = 67;
    private const int ProxyDhcpPort = 4011;
    private const int TftpResponseTimeoutMilliseconds = 2000;
    private const int TftpRetries = 5;
    private const int MaximumErrorCharacters = 1000;
    private const int AdditionalPollWaits = 11;
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private SiteLocalState? desired;
    private CancellationTokenSource? cycle;
    private TFTPServer? tftp;
    private readonly List<UdpClient> sockets = [];
    private readonly List<Task> receivers = [];
    private CancellationTokenSource? listenerStop;
    private SiteBootStatus status = new(0, false, false, null);
    public SiteBootStatus Status => Volatile.Read(ref status) with { ActiveTransfers = tftp?.ActiveTransfers ?? 0 };
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
        string? activeKey = null;
        var reservations = new NativePxeReservations();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SiteLocalState? state; CancellationTokenSource iteration;
                lock (sync) { state = desired; cycle = iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); }
                try
                {
                    var settings = state?.Configuration?.Boot;
                    if (settings is null || !settings.PxeEnabled && !settings.TftpEnabled)
                    {
                        await StopListenersAsync(); activeKey = null;
                        Volatile.Write(ref status, new(state?.Configuration?.Revision ?? 0, false, false, null));
                    }
                    else
                    {
                        settings.Validate();
                        using var http = SiteEnrollmentClient.CreateClient(state!, true);
                        http.Timeout = SynchronizationTimeout; http.MaxResponseContentBufferSize = MaximumManifestBytes;
                        var (manifest, directory) = await cache.SynchronizeAsync(http, iteration.Token);
                        if (settings.TftpEnabled)
                            foreach (var name in new[] { manifest.BiosBootFile, manifest.Efi32BootFile, manifest.Efi64BootFile, "autoexec.ipxe" })
                                if (!File.Exists(SiteBootPaths.Resolve(directory, name))) throw new InvalidDataException("Prepare all PXE boot files on the main server. Missing " + name);
                        var pxe = new PxeSettingsDto { Enabled = settings.PxeEnabled, Interface = settings.ListenAddress, NextServer = settings.NextServer,
                            BiosBootFile = manifest.BiosBootFile, Efi32BootFile = manifest.Efi32BootFile, Efi64BootFile = manifest.Efi64BootFile, EfiArm64BootFile = manifest.EfiArm64BootFile };
                        if (settings.PxeEnabled && pxe.Validate() is not null) throw new InvalidDataException("Invalid ProxyDHCP configuration.");
                        var key = System.Text.Json.JsonSerializer.Serialize(new { settings, directory, manifest.BiosBootFile, manifest.Efi32BootFile, manifest.Efi64BootFile, manifest.EfiArm64BootFile });
                        if (key != activeKey || receivers.Any(x => x.IsCompleted) || settings.TftpEnabled && tftp?.Active != true)
                        {
                            if (tftp?.ActiveTransfers > 0) throw new InvalidOperationException("Waiting for active TFTP transfers before applying boot changes.");
                            await StopListenersAsync(); activeKey = null;
                            iteration.Token.ThrowIfCancellationRequested();
                            if (settings.TftpEnabled)
                            {
                                tftp = new TFTPServer { EndPoint = new IPEndPoint(IPAddress.Parse(settings.ListenAddress), TftpPort),
                                    RootPath = directory, ResponseTimeout = TftpResponseTimeoutMilliseconds, Retries = TftpRetries, WindowSize = 1 };
                                tftp.Start();
                            }
                            if (settings.PxeEnabled)
                            {
                                listenerStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                var configuration = new PxePacketConfiguration(pxe) { Reservations = reservations };
                                foreach (var port in new[] { settings.ListenDhcp ? DhcpServerPort : 0, settings.ListenProxy ? ProxyDhcpPort : 0 }.Where(x => x != 0))
                                {
                                    var socket = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true, EnableBroadcast = true };
                                    sockets.Add(socket); socket.Client.Bind(new IPEndPoint(IPAddress.Parse(settings.ListenAddress), port));
                                    receivers.Add(ReceiveAsync(socket, port, configuration, listenerStop.Token));
                                }
                            }
                            activeKey = key;
                        }
                        reservations.ReplaceWithDefaults(manifest.Reservations.Select((x, index) => (index, x.Mac, new NativePxeTarget(x.DefinitionId, x.BuildId) { Architecture = x.Architecture })), "Synchronized from the main server.",
                            manifest.NativeDefault is { } nativeDefault ? new(nativeDefault.DefinitionId, nativeDefault.BuildId) : null,
                            manifest.NativeArm64Default is { } armDefault ? new(armDefault.DefinitionId, armDefault.BuildId) { Architecture = "arm64" } : null,
                            manifest.KnownMacs);
                        Volatile.Write(ref status, new(state!.Configuration!.Revision, sockets.Count > 0, tftp?.Active == true, null));
                        if (tftp?.ActiveTransfers is not > 0) cache.Cleanup(directory);
                    }
                }
                catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (activeKey is null) await StopListenersAsync();
                    var error = ex is IOException or InvalidOperationException or SocketException ? ex.Message : "Boot-file synchronization failed. Check the Site Service logs.";
                    Volatile.Write(ref status, Status with { PxeRunning = sockets.Count > 0 && receivers.All(x => !x.IsCompleted), TftpRunning = tftp?.Active == true, Error = error[..Math.Min(error.Length, MaximumErrorCharacters)] });
                    logger.LogWarning(ex, "Site Service boot configuration failed; retrying");
                }
                finally { lock (sync) { if (cycle == iteration) cycle = null; } iteration.Dispose(); }
                await Task.Delay(PollInterval, stoppingToken);
                // Poll files independently of configuration revisions (new WinPE builds and reservations).
                for (var i = 0; i < AdditionalPollWaits; i++)
                {
                    lock (sync) { if (desired?.Configuration?.Revision != state?.Configuration?.Revision || (desired is null) != (state is null)) break; }
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopListenersAsync(); }
    }
    private async Task ReceiveAsync(UdpClient socket, int port, PxePacketConfiguration configuration, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var request = await socket.ReceiveAsync(ct);
            try
            {
                var reply = PxePacketHandler.CreateReply(request.Buffer, request.RemoteEndPoint, port, configuration);
                if (reply is not null)
                {
                    logger.LogDebug("PXE request from {Source} MAC {Mac} on UDP {Port}; architecture {Architecture}; bootloader {BootFile}; reason: {Reason}",
                        request.RemoteEndPoint, Convert.ToHexString(request.Buffer.AsSpan(28, 6)), port, reply.Architecture, reply.BootFile ?? "(none)", reply.Reason);
                    await socket.SendAsync(reply.Data, reply.Destination, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
            { logger.LogDebug(ex, "Ignored invalid PXE request"); }
        }
    }
    private async Task StopListenersAsync()
    {
        listenerStop?.Cancel(); foreach (var socket in sockets) socket.Dispose();
        try { await Task.WhenAll(receivers); } catch (Exception ex) { logger.LogDebug(ex, "Boot listeners stopped"); }
        sockets.Clear(); receivers.Clear(); listenerStop?.Dispose(); listenerStop = null;
        tftp?.Dispose(); tftp = null;
    }
}
