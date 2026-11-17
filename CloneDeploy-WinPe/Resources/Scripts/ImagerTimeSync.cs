// Compiled by Windows PowerShell 5.1 in WinPE; no Windows Time service required.
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

public static class ImagerTimeSync
{
    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
            ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    }

    private static DateTime ReadTime(byte[] bytes, int offset)
    {
        long seconds = ReadUInt32(bytes, offset);
        // Unfold NTP's 2036 rollover into 1970-2106, independently of the broken local clock.
        if (seconds < 2208988800L) seconds += 4294967296L;
        long ticks = seconds * TimeSpan.TicksPerSecond +
            (long)((ulong)ReadUInt32(bytes, offset + 4) * TimeSpan.TicksPerSecond / 4294967296UL);
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
    }

    public static DateTime Query(string server)
    {
        const int timeoutMs = 3000;
        var lookup = Dns.GetHostAddressesAsync(server);
        if (!lookup.Wait(timeoutMs)) throw new TimeoutException("Time-server DNS lookup timed out.");
        Exception failure = null;
        var budget = Stopwatch.StartNew();
        foreach (var address in lookup.Result)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6) continue;
            int remaining = timeoutMs - (int)budget.ElapsedMilliseconds;
            if (remaining <= 0) break;
            try
            {
                using (var client = new UdpClient(address.AddressFamily))
                {
                    client.Client.ReceiveTimeout = remaining;
                    client.Client.SendTimeout = remaining;
                    client.Connect(new IPEndPoint(address, 123));
                    var request = new byte[48];
                    request[0] = 0x23; // NTPv4 client.
                    // An unpredictable transmit token is echoed in the reply's originate field.
                    using (var random = RandomNumberGenerator.Create())
                    {
                        var token = new byte[8];
                        random.GetBytes(token);
                        Array.Copy(token, 0, request, 40, 8);
                    }
                    var elapsed = Stopwatch.StartNew();
                    client.Send(request, request.Length);
                    IPEndPoint remote = null;
                    var reply = client.Receive(ref remote);
                    elapsed.Stop();
                    if (reply.Length < 48 || (reply[0] & 7) != 4 ||
                        ((reply[0] >> 3) & 7) < 3 || ((reply[0] >> 3) & 7) > 4 ||
                        (reply[0] >> 6) == 3 || reply[1] < 1 || reply[1] > 15)
                        throw new InvalidOperationException("Time server returned an invalid or unsynchronized reply.");
                    for (int i = 0; i < 8; i++)
                        if (reply[24 + i] != request[40 + i])
                            throw new InvalidOperationException("Time reply does not match this request.");
                    if ((ReadUInt32(reply, 32) == 0 && ReadUInt32(reply, 36) == 0) ||
                        (ReadUInt32(reply, 40) == 0 && ReadUInt32(reply, 44) == 0))
                        throw new InvalidOperationException("Time server returned an empty timestamp.");
                    var received = ReadTime(reply, 32);
                    var transmitted = ReadTime(reply, 40);
                    var processing = transmitted - received;
                    if (processing < TimeSpan.Zero || processing.TotalMilliseconds > elapsed.Elapsed.TotalMilliseconds + 100)
                        throw new InvalidOperationException("Time server returned inconsistent timestamps.");
                    return transmitted.AddMilliseconds(Math.Max(0, (elapsed.Elapsed.TotalMilliseconds - processing.TotalMilliseconds) / 2));
                }
            }
            catch (Exception ex) { failure = ex; }
        }
        throw new InvalidOperationException("No usable NTP reply from " + server + ".", failure);
    }
}
