using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal sealed class AgentWakeRelay
{
    private const int MaximumMacAddresses = 128;
    private const int RepeatDelayMilliseconds = 100;

    private const int MaximumRememberedRequests = 128;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, AgentWakeReply> completed = [];
    private readonly Func<IEnumerable<string>> networks;
    private readonly Func<IPAddress, IPAddress, string[], Task<string[]>> send;
    public AgentWakeRelay() : this(LocalNetworks, SendAsync) { }
    internal AgentWakeRelay(Func<IEnumerable<string>> networks, Func<IPAddress, IPAddress, string[], Task<string[]>> send)
    { this.networks = networks; this.send = send; }

    internal async Task<AgentWakeReply> ExecuteAsync(AgentWakeRequest request, Guid deviceGuid)
    {
        AgentWakeReply Failure(string message) => new(request.RequestId, [], message);
        if (request.RequestId == Guid.Empty || request.DeviceGuid != deviceGuid || request.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2) || request.Macs is not { Length: > 0 and <= MaximumMacAddresses }
            || request.Macs.Any(x => WakeNetwork.Mac(x) is null)
            || !WakeNetwork.TryParse(request.Subnet, out var subnet, out _, out _)) return Failure("Invalid or expired wake-up request.");
        if (!await gate.WaitAsync(0)) return Failure("The wake-up relay is busy.");
        try
        {
            if (completed.TryGetValue(request.RequestId, out var previous)) return previous;
            AgentWakeReply reply = Failure("This agent has no active interface on the requested subnet.");
            foreach (var local in networks())
            {
                if (!WakeNetwork.TryParse(local, out var localSubnet, out var address, out var broadcast) || localSubnet != subnet) continue;
                var sent = await send(address, broadcast, request.Macs.Select(x => WakeNetwork.Mac(x)!).Distinct().ToArray());
                reply = new(request.RequestId, sent, sent.Length == 0 ? "No magic packets were sent." : "Magic packets sent.");
                break;
            }
            if (completed.Count >= MaximumRememberedRequests) completed.Remove(completed.Keys.First());
            completed[request.RequestId] = reply;
            return reply;
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or InvalidOperationException)
        { return Failure("The relay could not send magic packets on this subnet."); }
        finally { gate.Release(); }
    }

    private static IEnumerable<string> LocalNetworks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(x => $"{x.Address}/{x.PrefixLength}");

    private static async Task<string[]> SendAsync(IPAddress local, IPAddress broadcast, string[] macs)
    {
        using var socket = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = new HashSet<string>();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var mac in macs)
            {
                try { await socket.SendAsync(WakeNetwork.Packet(mac), new IPEndPoint(broadcast, 9), timeout.Token); sent.Add(mac); }
                catch (SocketException) { }
                catch (OperationCanceledException) { return sent.ToArray(); }
            }
            if (pass == 0) await Task.Delay(RepeatDelayMilliseconds);
        }
        return sent.ToArray();
    }
}
