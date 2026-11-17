using System.Diagnostics;
using System.Net.NetworkInformation;

namespace CloneDeploy_Imager.Imaging;

// Adapter traffic, including protocol overhead and other applications. Defaults to outgoing traffic.
/// <summary>
/// Samples active network adapters to estimate transferred bytes per second for the progress display.
/// </summary>
internal sealed class NetworkTransferRate
{
    private readonly Func<Dictionary<string, long>> read;
    private readonly Func<double> seconds;
    private Dictionary<string, long>? previous;
    private double sampledAt = double.NegativeInfinity;
    private double? rate;
    public NetworkTransferRate(Func<Dictionary<string, long>>? read = null, Func<double>? seconds = null)
    {
        this.read = read ?? ReadAdapters;
        this.seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }
    public static NetworkTransferRate ForReceive() => new(() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .ToDictionary(x => x.Id, x => x.GetIPStatistics().BytesReceived));

    public double? Sample()
    {
        var now = seconds();
        if (now - sampledAt < 2) return rate;
        try
        {
            var current = read();
            rate = null;
            if (previous is not null && current.Count > 0 && current.Keys.ToHashSet().SetEquals(previous.Keys) &&
                current.All(x => x.Value >= previous[x.Key]))
                rate = current.Sum(x => (double)(x.Value - previous[x.Key])) / (now - sampledAt);
            previous = current;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or System.Security.SecurityException)
        { previous = null; rate = null; }
        sampledAt = now;
        return rate;
    }
    private static Dictionary<string, long> ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .ToDictionary(x => x.Id, x => x.GetIPStatistics().BytesSent);
}
