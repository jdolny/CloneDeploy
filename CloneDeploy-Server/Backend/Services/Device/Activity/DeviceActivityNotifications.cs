using System.Threading.Channels;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>LISTEN waits on PostgreSQL notifications; it does not poll device tables.</summary>
public sealed class DeviceActivityNotifications(IConfiguration configuration, DeviceActivityFeed feed,
    ILogger<DeviceActivityNotifications> logger) : BackgroundService
{
    private readonly Channel<int> pending = Channel.CreateBounded<int>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int refreshAll;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ListenAsync(stoppingToken), DeliverAsync(stoppingToken));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("CloneDeploy-Server-Database")) { KeepAlive = 30 };
                await using var connection = new NpgsqlConnection(settings.ConnectionString);
                await connection.OpenAsync(ct);
                await using var schemaCommand = new NpgsqlCommand("SELECT current_schema()", connection);
                var prefix = (string)(await schemaCommand.ExecuteScalarAsync(ct))! + ":";
                connection.Notification += (_, args) =>
                {
                    if (args.Payload.StartsWith(prefix, StringComparison.Ordinal) &&
                        int.TryParse(args.Payload.AsSpan(prefix.Length), out var id) && !pending.Writer.TryWrite(id))
                        Interlocked.Exchange(ref refreshAll, 1);
                };
                await using var command = new NpgsqlCommand("LISTEN device_activity_changed", connection);
                await command.ExecuteNonQueryAsync(ct);
                await pending.Writer.WriteAsync(0, ct); // Recover changes missed while disconnected.
                while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Device activity notifications disconnected; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task DeliverAsync(CancellationToken ct)
    {
        while (await pending.Reader.WaitToReadAsync(ct))
        {
            await Task.Delay(100, ct); // Coalesce a deployment's related changes into one batch.
            var ids = new HashSet<int>();
            while (pending.Reader.TryRead(out var id)) ids.Add(id);
            if (Interlocked.Exchange(ref refreshAll, 0) != 0) ids.Add(0);
            try { await feed.RefreshAsync(ids.ToArray(), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Device activity refresh failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                Interlocked.Exchange(ref refreshAll, 1);
                pending.Writer.TryWrite(0);
            }
        }
    }
}
