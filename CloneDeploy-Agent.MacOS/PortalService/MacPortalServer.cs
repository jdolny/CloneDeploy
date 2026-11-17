using System.Net.Sockets;
using System.Text.Json;
namespace CloneDeploy_Agent.MacOS;

/// <summary>Read-only local status, available to local users. Never accepts commands.</summary>
public sealed class MacPortalServer(Func<MacPortalStatus> snapshot)
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var directory = Path.GetDirectoryName(MacPortalStatus.SocketPath)!;
        var bound = false;
        try
        {
            // /var/run is a system-owned alias on macOS; our child must never be redirected.
            if (new DirectoryInfo(directory).LinkTarget is not null)
                throw new IOException("Portal runtime directory is a symbolic link.");
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Delete(MacPortalStatus.SocketPath); // Single daemon lock already held.
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(MacPortalStatus.SocketPath));
            bound = true;
            File.SetUnixFileMode(MacPortalStatus.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            listener.Listen(16);
            while (!ct.IsCancellationRequested)
            {
                using var peer = await listener.AcceptAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using var stream = new NetworkStream(peer, ownsSocket: false);
                    await JsonSerializer.SerializeAsync(stream, snapshot(), cancellationToken: timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                { if (ct.IsCancellationRequested) break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        { Console.Error.WriteLine($"Portal status unavailable ({ex.GetType().Name}). Device service continues."); }
        finally { if (bound) { try { File.Delete(MacPortalStatus.SocketPath); } catch (IOException) { } } }
    }
}
