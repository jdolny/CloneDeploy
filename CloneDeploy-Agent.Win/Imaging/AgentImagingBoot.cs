using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Stages verified WinPE on an unencrypted system volume and arms one boot while preserving normal Windows startup.</summary>
public sealed partial class AgentImagingBoot(
    AgentOptions options,
    AgentIdentity identity,
    PortalNotificationJournal messages,
    ILogger<AgentImagingBoot> logger) : BackgroundService
{
    private const long MaximumBootFileBytes = 8L * 1024 * 1024 * 1024;
    private const long RequiredFreeSpaceReserveBytes = 512L * 1024 * 1024;
    private const int DownloadBufferBytes = 128 * 1024;

    private const int MaximumPlanPolls = 12;
    private const int MaximumFailureDetailLength = 1900;

    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ImagingTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<Guid> requests = Channel.CreateBounded<Guid>(1);
    private int pending;
    private volatile bool ready;

    private string Root => Path.Combine(Path.GetFullPath(options.StateDirectory), "WinPe");

    private string Journal => Path.Combine(Root, "pending.json");

    private sealed record PendingBoot(Guid DeploymentId, Guid Loader, Guid Ramdisk)
    {
        public Guid? CaptureUploadId { get; init; }
    }

    public async Task<AgentImagingReply> ReceiveAsync(AgentImagingCommand request, Guid deviceGuid)
    {
        try
        {
            if (!ready || request.DeviceGuid != deviceGuid || request.ExpiresAtUtc < DateTimeOffset.UtcNow
                || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                throw new InvalidOperationException("Agent is not ready or the imaging request has expired.");

            using var timeout = new CancellationTokenSource(AdmissionTimeout);
            ValidateArchitecture(request.Architecture, RuntimeInformation.OSArchitecture);
            var route = await CheckAsync(request.ServerUrl, timeout.Token);

            if (request.DeploymentId is { } id)
            {
                if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
                    throw new InvalidOperationException("An imaging preparation is already active.");

                if (!requests.Writer.TryWrite(id))
                {
                    Interlocked.Exchange(ref pending, 0);
                    throw new InvalidOperationException("Could not queue imaging preparation.");
                }

                return new(request.RequestId, true, "Preparing WinPE. Progress is available in Imaging Tasks.");
            }

            return new(request.RequestId, true, route);
        }
        catch (Exception ex)
        {
            return new(
                request.RequestId,
                false,
                ex.Message.Length > MaximumFailureDetailLength
                    ? ex.Message[..MaximumFailureDetailLength]
                    : ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Directory.CreateDirectory(Root);
            PolicyActionRunner.RejectReparsePoints(Root);

            if (File.Exists(Journal))
            {
                var previous = JsonSerializer.Deserialize<PendingBoot>(await File.ReadAllTextAsync(
                    Journal,
                    stoppingToken))!;
                await CleanupAsync(previous, stoppingToken);

                try
                {
                    using var client = Client();
                    if (previous.CaptureUploadId is { } captureId)
                    {
                        using var response = await client.DeleteAsync($"api/agent/v1/prepared-capture/{captureId}", stoppingToken);
                        response.EnsureSuccessStatusCode();
                    }
                    else await ProgressAsync(
                        client,
                        previous.DeploymentId,
                        "Failed",
                        "Windows resumed before imaging completed. Local boot entries were removed; review and retry the deployment.",
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not report recovered imaging preparation: {ex.Message}");
                }

                if (previous.CaptureUploadId is not null) DeleteCaptureFiles();
                File.Delete(Journal);
            }

            ready = true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Local imaging boot recovery failed; agent imaging is disabled until recovery succeeds");

            return;
        }

        await foreach (var id in requests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(id, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Could not initialize imaging preparation for {id}");
            }
            finally
            {
                Interlocked.Exchange(ref pending, 0);
            }
        }
    }

    private HttpClient Client()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        AgentServerTrust.Configure(handler, options);
        handler.ClientCertificates.Add(identity.GetClientCertificate()
                                       ?? throw new IOException("Agent is not enrolled."));

        return new HttpClient(new CloneDeploy_Agent.Contracts.SiteDownloadClient(new AgentResponseVerification(handler, options)))
        {
            BaseAddress = options.GetServerUri(),
            Timeout = DownloadTimeout
        };
    }

    private static async Task<AgentImagingReview> PlanAsync(HttpClient client, Guid id, CancellationToken ct) =>
        await client.GetFromJsonAsync<AgentImagingReview>($"api/agent/v1/imaging-boot/{id}", ct)
        ?? throw new IOException("Deployment is unavailable.");

    private static async Task ProgressAsync(
        HttpClient client,
        Guid id,
        string stage,
        string detail,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/agent/v1/imaging-boot/{id}/progress",
            new AgentBootProgress(stage, detail),
            ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> CheckAsync(string serverUrl, CancellationToken ct)
    {
        using var user = WindowsIdentity.GetCurrent();

        if (!user.IsSystem || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new InvalidOperationException("Agent imaging requires an installed SYSTEM service on x64 or ARM64 Windows.");

        if (!GetFirmwareType(out var firmware) || firmware is not (1 or 2)
            || (RuntimeInformation.OSArchitecture == Architecture.Arm64 && firmware != 2))
            throw new InvalidOperationException("Unsupported firmware boot mode.");

        if (Path.GetPathRoot(Root) != Path.GetPathRoot(Environment.SystemDirectory))
            throw new InvalidOperationException("Agent state and WinPE staging must be on the Windows volume.");

        await RunToolAsync("bcdedit.exe", ["/enum", "{bootmgr}"], ct);
        var mainRoute = await WiredRouteAsync(options.GetServerUri(), ct);
        var target = new Uri(serverUrl);

        if (target.Scheme != "https" || target.UserInfo.Length != 0)
            throw new InvalidOperationException("Invalid WinPE server URL.");

        var bootRoute = target.Authority == options.GetServerUri().Authority
            ? mainRoute
            : await WiredRouteAsync(target, ct);

        return $"Ethernet route to agent server: {mainRoute}. WinPE server: {bootRoute}.";
    }

    internal static async Task<string> WiredRouteAsync(Uri server, CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(server.DnsSafeHost, server.Port, ct);
        var local = ((IPEndPoint)socket.LocalEndPoint!).Address;

        if (local.IsIPv4MappedToIPv6)
            local = local.MapToIPv4();

        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                                                                 &&
                                                                                 n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local)));

        if (nic is null || !IsWired(nic.NetworkInterfaceType))
            throw new InvalidOperationException($"The route to {server.Host} is not Ethernet. Attach a wired adapter with server access before imaging.");

        return $"{nic.Name} ({local})";
    }

    internal static void ValidateArchitecture(string architecture, Architecture nativeArchitecture)
    {
        var expected = nativeArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        if (expected is null || architecture != expected)
            throw new InvalidOperationException("WinPE architecture does not match this computer. Refresh inventory and reload the imaging review.");
    }

    internal static bool IsWired(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT;

    private async Task RunAsync(Guid id, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ImagingTimeout);
        var ct = timeout.Token;
        using var client = Client();
        PendingBoot? boot = null;
        var gate = false;

        try
        {
            gate = await AgentExecutionGate.Gate.WaitAsync(0, ct);

            if (!gate)
                throw new InvalidOperationException("An agent task or policy is running. Retry after it finishes.");

            var plan = await PlanAsync(client, id, ct);
            ValidateArchitecture(plan.Architecture, RuntimeInformation.OSArchitecture);
            boot = new(id, Guid.NewGuid(), Guid.NewGuid());
            await File.WriteAllTextAsync($"{Journal}.new", JsonSerializer.Serialize(boot), ct);
            File.Move($"{Journal}.new", Journal, true);
            await CheckAsync(plan.ServerUrl, ct);

            if (plan.DeviceGuid != identity.State.DeviceGuid || plan.Files.Length != 2
                                                             ||
                                                             !plan.Files.Select(f => f.Name).Order().SequenceEqual(new[] { "boot.sdi", "boot.wim" })
                                                             ||
                                                             plan.Files.Any(f => f.Size <= 0
                                                                                 || f.Size > MaximumBootFileBytes
                                                                                 || f.Sha256.Length != 64
                                                                                 || !f.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Invalid WinPE manifest.");

            var folder = Path.Combine(Root, plan.BuildId.ToString("N"));
            Directory.CreateDirectory(folder);
            PolicyActionRunner.RejectReparsePoints(folder);

            if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < plan.Files.Sum(f => f.Size) + RequiredFreeSpaceReserveBytes)
                throw new IOException("Insufficient free disk space to stage WinPE.");

            foreach (var file in plan.Files)
            {
                await ProgressAsync(client, id, "Preparing", $"Downloading / verifying {file.Name}", ct);
                var path = Path.Combine(folder, file.Name);
                PolicyActionRunner.RejectReparsePoints(path);

                if (await MatchesAsync(path, file, ct))
                    continue;

                var partial = $"{path}.part";
                PolicyActionRunner.RejectReparsePoints(partial);
                using var response = await AgentResponseVerification.DownloadAsync(client,
                    $"api/agent/v1/imaging-boot/{id}/files/{file.Name}",
                    file.Sha256,
                    ct);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    await CopyVerifiedAsync(input, output, file, ct);
                File.Move(partial, path, true);
            }

            await PlanAsync(client, id, ct); // Cancellation before any boot mutation.
            await ProgressAsync(client, id, "Preparing", "Preparing one-time WinPE boot", ct);
            GetFirmwareType(out var firmware);

            foreach (var args in BootCommands(boot.Loader, boot.Ramdisk, folder, firmware == 2))
                await RunToolAsync("bcdedit.exe", args, ct);

            messages.Enqueue(
                id,
                Guid.Empty,
                "Imaging restart",
                $"This computer will restart in 60 seconds to deploy {plan.ImageName}. Save your work and keep Ethernet connected.",
                60);
            await ProgressAsync(
                client,
                id,
                "Preparing",
                "WinPE verified. Restarting in 60 seconds; keep Ethernet connected.",
                ct);

            for (var i = 0; i < MaximumPlanPolls; i++)
            {
                await Task.Delay(PlanPollInterval, ct);
                await PlanAsync(client, id, ct);
            }

            await CheckAsync(plan.ServerUrl, ct);
            await RunToolAsync("bcdedit.exe", ["/bootsequence", $"{{{boot.Loader}}}", "/addfirst"], ct);
            await ProgressAsync(client, id, "Restarting", "Restarting into WinPE; waiting for Imager", ct);
            await RunToolAsync("shutdown.exe", ["/r", "/t", "0", "/f", "/d", "p:4:1", "/c", "CloneDeploy imaging"], ct);
            // Retain the execution gate until Windows stops us; never run another task between boot arming and restart.
            await Task.Delay(Timeout.InfiniteTimeSpan, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Agent imaging preparation failed for {id}");
            using var cleanup = new CancellationTokenSource(CleanupTimeout);

            if (boot is not null)
            {
                try
                {
                    await CleanupAsync(boot, cleanup.Token);
                    File.Delete(Journal);
                }
                catch (Exception recovery)
                {
                    ready = false;
                    logger.LogError(recovery, "Boot cleanup needs attention; further agent imaging disabled");
                }
            }

            try
            {
                await ProgressAsync(
                    client,
                    id,
                    "Failed",
                    ex.Message.Length > MaximumFailureDetailLength
                        ? ex.Message[..MaximumFailureDetailLength]
                        : ex.Message,
                    cleanup.Token);
            }
            catch (Exception report)
            {
                logger.LogWarning($"Could not report imaging failure: {report.Message}");
            }
        }
        finally
        {
            if (gate) AgentExecutionGate.Gate.Release();
        }
    }

    internal static IEnumerable<string[]> BootCommands(Guid loader, Guid ramdisk, string folder, bool uefi)
    {
        var l = $"{{{loader}}}";
        var r = $"{{{ramdisk}}}";
        var volume = Path.GetPathRoot(folder)!.TrimEnd('\\');
        var path = folder[volume.Length..].TrimEnd('\\');
        yield return ["/create", r, "/device", "/d", "CloneDeploy WinPE ramdisk"];
        yield return ["/set", r, "ramdisksdidevice", $"partition={volume}"];
        yield return ["/set", r, "ramdisksdipath", $"{path}\\boot.sdi"];
        yield return ["/create", l, "/application", "osloader", "/d", "CloneDeploy Imaging"];
        yield return ["/set", l, "device", $"ramdisk=[{volume}]{path}\\boot.wim,{r}"];
        yield return ["/set", l, "osdevice", $"ramdisk=[{volume}]{path}\\boot.wim,{r}"];
        yield return
        [
            "/set", l, "path", uefi
                ? @"\windows\system32\boot\winload.efi"
                : @"\windows\system32\boot\winload.exe"
        ];
        yield return ["/set", l, "systemroot", @"\windows"];
        yield return ["/set", l, "winpe", "yes"];
        yield return ["/set", l, "detecthal", "yes"];
    }

    private static async Task RunToolAsync(string tool, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool));

        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        var result = await new PolicyProcess().RunAsync(start, 1, ct);

        if (result.ExitCode != 0)
            throw new IOException($"{tool} failed ({result.ExitCode}): {result.Output}{result.Error}");
    }

    private static async Task CleanupAsync(PendingBoot boot, CancellationToken ct)
    {
        await RunToolAsync("bcdedit.exe", ["/enum", "{bootmgr}"], ct);

        foreach (var id in new[] { boot.Loader, boot.Ramdisk })
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"));
            start.ArgumentList.Add("/enum");
            start.ArgumentList.Add($"{{{id}}}");
            var existing = await new PolicyProcess().RunAsync(start, 1, ct);

            if (existing.ExitCode == 0)
                await RunToolAsync("bcdedit.exe", ["/delete", $"{{{id}}}"], ct);
        }
    }

    private static async Task<bool> MatchesAsync(string path, AgentBootFile file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
            return false;

        await using var stream = File.OpenRead(path);

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task CopyVerifiedAsync(
        Stream source,
        Stream destination,
        AgentBootFile file,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DownloadBufferBytes];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;

            if (total > file.Size)
                throw new InvalidDataException("WinPE download exceeds its manifest size.");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total != file.Size
            || !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WinPE download failed size/hash verification.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmware);
}
