using System.Net;
using CodePlex.JPMikkers.TFTP;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Owns the read-only TFTP server and applies persisted settings every five seconds.</summary>
public sealed class TftpWorker(IServiceScopeFactory scopes, ILogger<TftpWorker> logger, TftpRootDirectory directory) : BackgroundService
{
    private TFTPServer? _server;
    public IReadOnlyList<CodePlex.JPMikkers.TFTP.SessionLogEntry> Transfers =>
        Volatile.Read(ref _server)?.SessionLog.GetHistory() ?? [];
    public int ActiveTransfers => Volatile.Read(ref _server)?.ActiveTransfers ?? 0;
    private TftpStatusDto _status = new("Starting", "Waiting for settings.");
    public TftpStatusDto Status => Volatile.Read(ref _status);
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
        TftpSettingsDto? active = null;
        TFTPServer? server = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    TftpSettingsDto settings;
                    try { settings = await scope.ServiceProvider.GetRequiredService<TftpSettingsStore>().ReadAsync(stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException && server?.Active == true)
                    {
                        SetStatus("Running", "Using the last loaded settings; settings refresh failed. See server logs.");
                        logger.LogWarning(ex, "TFTP settings refresh failed; keeping the active server");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                    if (settings != active || settings.Enabled && server?.Active != true)
                    {
                        Volatile.Write(ref _server, null);
                        server?.Dispose();
                        server = null;
                        active = null;
                        if (!settings.Enabled) SetStatus("Disabled", "TFTP is disabled.");
                        else
                        {
                            if (settings.Validate() is { } error) throw new InvalidOperationException(error);
                            directory.EnsureExists();
                            server = new TFTPServer
                            {
                                EndPoint = new IPEndPoint(IPAddress.Parse(settings.Interface), 69),
                                RootPath = directory.RootPath,
                                ResponseTimeout = settings.ResponseTimeoutMilliseconds,
                                Retries = settings.Retries,
                                ReadStreamOverride = (relative, source, client) =>
                                {
                                    using var readScope = scopes.CreateScope();
                                    var delivery = readScope.ServiceProvider.GetService<SharedContentDelivery>();
                                    return delivery is null ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) :
                                        delivery.OpenBootAsync(relative, source, client.Address, stoppingToken).GetAwaiter().GetResult();
                                },
                                WindowSize = 1
                            };
                            server.OnTrace += (_, entry) =>
                            {
                                var failed = entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) || entry.Message.Contains("Aborted", StringComparison.OrdinalIgnoreCase);
                                Activity.Add(failed ? "Warning" : "Information", entry.Message);
                                if (failed) logger.LogWarning("TFTP: {Message}", entry.Message);
                                else logger.LogDebug("TFTP: {Message}", entry.Message);
                            };
                            server.Start();
                            Volatile.Write(ref _server, server);
                        }
                        active = settings;
                    }
                    if (server?.Active == true)
                        SetStatus("Running", $"Listening on {active!.Interface}:69 (UDP); read-only; {server.ActiveTransfers} active transfers.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Volatile.Write(ref _server, null);
                    server?.Dispose();
                    server = null;
                    active = null;
                    SetStatus("Error", ex is InvalidOperationException ? ex.Message : "Unable to start or refresh TFTP. Check the root directory, listen address, port, and server logs.");
                    logger.LogError(ex, "TFTP service failed; retrying in five seconds");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref _server, null);
            server?.Dispose();
            SetStatus("Stopped", "Application is stopping.");
        }
    }
}
