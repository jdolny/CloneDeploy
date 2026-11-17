using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CloneDeploy.SiteServices;

// All process arguments come from validated session settings. Never invoke a command shell.
public static class UdpcastSender
{
    private const int FirstMulticastOctet = 224;
    private const int AdministrativeMulticastOctet = 239;
    private const int MinimumPort = 1024;
    private const int MaximumPortBase = 65_534;
    private const int MaximumReceivers = 1000;
    private const int MaximumMegabitsPerSecond = 100_000;
    private const int MaximumTtl = 32;
    private const int StartTimeoutSeconds = 3600;
    private const int RetriesUntilDrop = 100;
    private const int MaximumDiagnosticLines = 8;
    private const int MaximumDiagnosticCharacters = 256;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(12);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    public static void Validate(MulticastConnection connection)
    {
        if (connection.SessionId == Guid.Empty || !IPAddress.TryParse(connection.SenderAddress, out var sender) ||
            sender.AddressFamily != AddressFamily.InterNetwork || sender.GetAddressBytes()[0] is 0 or >= FirstMulticastOctet ||
            !IPAddress.TryParse(connection.GroupAddress, out var group) || group.AddressFamily != AddressFamily.InterNetwork ||
            group.GetAddressBytes()[0] != AdministrativeMulticastOctet || connection.Port is < MinimumPort or > MaximumPortBase || connection.Port % 2 != 0)
            throw new InvalidDataException("Multicast requires a sender IPv4 address, a 239.x.x.x group, and an even UDP port between 1024 and 65534.");
    }

    public static string Executable => OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "Tools", "udpcast", RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "a64" : "x64", "udp-sender.exe")
        : "/usr/bin/udp-sender";

    public static async Task RunAsync(MulticastOffer offer, Stream input,
        Func<MulticastProgress, CancellationToken, Task<MulticastControl>> heartbeat, CancellationToken ct)
    {
        Validate(offer.Connection);
        if (offer.Receivers is < 1 or > MaximumReceivers || offer.MegabitsPerSecond is < 1 or > MaximumMegabitsPerSecond || offer.Ttl is < 1 or > MaximumTtl)
            throw new InvalidDataException("Invalid multicast sender settings.");
        var address = IPAddress.Parse(offer.Connection.SenderAddress);
        if (!NetworkInterface.GetAllNetworkInterfaces().Any(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address))))
            throw new InvalidDataException("The multicast sender address must belong to this host.");
        if (!File.Exists(Executable)) throw new FileNotFoundException("udp-sender is missing. On Linux install udpcast at /usr/bin/udp-sender.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(SessionTimeout);
        using var process = new Process { StartInfo = new(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "--nopointopoint", "--nokbd", "--no-progress", "--interface", offer.Connection.SenderAddress,
                     "--mcast-rdv-address", offer.Connection.SenderAddress,
                     "--portbase", offer.Connection.Port.ToString(CultureInfo.InvariantCulture), "--mcast-data-address", offer.Connection.GroupAddress,
                     "--min-receivers", offer.Receivers.ToString(CultureInfo.InvariantCulture), "--max-bitrate", offer.MegabitsPerSecond.ToString(CultureInfo.InvariantCulture) + "m",
                     "--ttl", offer.Ttl.ToString(CultureInfo.InvariantCulture), "--start-timeout", StartTimeoutSeconds.ToString(CultureInfo.InvariantCulture), "--retries-until-drop", RetriesUntilDrop.ToString(CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var kill = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var transmitting = 0;
        var diagnostics = new Queue<string>();
        var read = Read();
        var monitor = Monitor();
        // Anonymous process pipes use synchronous handles on Windows. Start the control
        // loop before feeding the pipe: UDPcast may not read stdin until receivers join.
        var copy = Task.Run(Copy, stop.Token);
        try
        {
            await process.WaitForExitAsync(stop.Token);
            await read;
            if (process.ExitCode != 0) throw new IOException("udp-sender failed: " + string.Join(" ", diagnostics));
            await copy;
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested)
        {
            if (monitor.IsFaulted) await monitor;
            if (copy.IsFaulted) await copy;
            throw new OperationCanceledException("Multicast sender stopped: " + string.Join(" ", diagnostics.ToArray()), ex, stop.Token);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(copy, read, monitor); } catch (Exception) when (stop.IsCancellationRequested) { }
        }
        async Task Copy()
        {
            try { await input.CopyToAsync(process.StandardInput.BaseStream, stop.Token); process.StandardInput.Close(); }
            catch { stop.Cancel(); throw; }
        }
        async Task Read()
        {
            while (await process.StandardError.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.StartsWith("Starting transfer:", StringComparison.Ordinal)) Interlocked.Exchange(ref transmitting, 1);
                if (diagnostics.Count == MaximumDiagnosticLines) diagnostics.Dequeue();
                diagnostics.Enqueue(line[..Math.Min(MaximumDiagnosticCharacters, line.Length)]);
            }
        }
        async Task Monitor()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await heartbeat(new(Volatile.Read(ref transmitting) != 0, false, null), stop.Token);
                    await Task.Delay(HeartbeatInterval, stop.Token);
                }
            }
            catch { stop.Cancel(); throw; }
        }
    }
}
