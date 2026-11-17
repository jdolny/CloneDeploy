using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CloneDeploy_Imager.Api;

internal sealed record MulticastConnection(Guid SessionId, string SenderAddress, string GroupAddress, int Port);
internal sealed record MulticastChoice(Guid Id, string Name, string Image, string SenderAddress, int ExpectedReceivers);

internal sealed partial class ImagingClient
{
    private const int MulticastReceiverPollMilliseconds = 100;
    private const int MulticastStartTimeoutSeconds = 3600;
    private const int MulticastReceiveTimeoutSeconds = 120;

    internal static string MulticastReceiverExecutable => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "CloneDeploy", "Tools", "udpcast",
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "a64" : "x64", "udp-receiver.exe");
    public async Task<List<MulticastChoice>> MulticastSessionsAsync(CancellationToken ct)
    {
        using var response = await UploadRequest(HttpMethod.Get, "deployment/multicast", null, ct);
        return await response.Content.ReadFromJsonAsync<List<MulticastChoice>>(ct) ?? [];
    }
    public async Task<Guid> JoinMulticastAsync(Guid sessionId, CancellationToken ct)
    {
        if (Device is null) throw new InvalidOperationException("Link this device before joining multicast.");
        using var content = JsonContent.Create(new { deviceLinkToken = Device.LinkToken, sessionId });
        using var response = await UploadRequest(HttpMethod.Post, "deployment/multicast/join", content, ct);
        return (await response.Content.ReadFromJsonAsync<PreparedDeployment>(ct))?.DeploymentId ?? throw new InvalidDataException("Missing multicast deployment.");
    }

    private async Task ReceiveMulticastAsync(DeploymentExecution image, Stream destination, CancellationToken ct)
    {
        var connection = image.Multicast ?? throw new InvalidDataException("Missing multicast connection.");
        if (connection.SessionId == Guid.Empty || !IPAddress.TryParse(connection.SenderAddress, out var sender) || sender.AddressFamily != AddressFamily.InterNetwork ||
            connection.Port is < MulticastPortLimits.MinimumReceiverPort or > MulticastPortLimits.MaximumReceiverPort || connection.Port % 2 != 0)
            throw new InvalidDataException("Invalid multicast connection.");
        var executable = MulticastReceiverExecutable;
        if (!File.Exists(executable)) throw new FileNotFoundException("The matching udp-receiver binary is missing from WinPE.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true } };
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(new IPEndPoint(sender, connection.Port + 1));
        var interfaceAddress = ((IPEndPoint)route.LocalEndPoint!).Address.ToString();
        foreach (var arg in new[] { "--no-progress", "--portbase", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--interface", interfaceAddress,
                     "--mcast-rdv-address", connection.SenderAddress, "--start-timeout", MulticastStartTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--receive-timeout", MulticastReceiveTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var kill = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        long received = 0;
        var connected = 0;
        var readErrors = ReadErrors();
        var keyboard = Keyboard();
        try
        {
            // Always verify multicast: a UDP rendezvous is not an authenticated source of image bytes.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileTransferBufferBytes];
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, stop.Token);
                if (count == 0) break;
                Interlocked.Add(ref received, count);
                if (received > image.SizeBytes) throw new InvalidDataException("Multicast image exceeds its published size.");
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), stop.Token);
            }
            await process.WaitForExitAsync(stop.Token);
            if (process.ExitCode != 0 || received != image.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Multicast image verification failed. Deployment cannot complete.");
            await destination.FlushAsync(stop.Token);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(readErrors, keyboard); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        async Task ReadErrors()
        {
            while (await process.StandardError.ReadLineAsync(stop.Token) is { } line)
            {
                log.Write("udp-receiver: " + line);
                if (line.Contains("Connected as", StringComparison.OrdinalIgnoreCase)) Volatile.Write(ref connected, 1);
            }
        }
        async Task Keyboard()
        {
            while (!stop.IsCancellationRequested && Interlocked.Read(ref received) == 0)
            {
                if (Volatile.Read(ref connected) != 0 && !Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                {
                    try
                    {
                        using var response = await DeploymentRequest(HttpMethod.Post, "multicast/start", null, stop.Token);
                        // Let UDPcast send its own GO packet from the already connected receiver socket.
                        await process.StandardInput.WriteLineAsync("".AsMemory(), stop.Token);
                        await process.StandardInput.FlushAsync(stop.Token);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { log.Failure("Multicast early start", ex); }
                }
                await Task.Delay(MulticastReceiverPollMilliseconds, stop.Token);
            }
        }
    }
}
