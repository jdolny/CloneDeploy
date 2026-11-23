using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Owns downloaded media and disk operations inside the privileged service.</summary>
public sealed class AgentUsbTool(AgentOptions options, AgentAdminSession admin, ILogger<AgentUsbTool> logger) : IDisposable
{
    private const int DownloadBufferBytes = 1024 * 1024;
    private const int BytesPerMiB = 1024 * 1024;

    private static class Operation
    {
        public const string Status = "Status";
        public const string Cancel = "Cancel";
        public const string Catalog = "Catalog";
        public const string Download = "Download";
        public const string Drives = "Drives";
        public const string Write = "Write";
    }

    private sealed class Job(string sid, int session, Guid buildId, CancellationTokenSource stop)
    {
        public string Architecture { get; init; } = "x64";
        public readonly string Sid = sid;
        public readonly int Session = session;
        public readonly CancellationTokenSource Stop = stop;
        public AgentUsbStatus Status = new(PortalToolStatus.Running, "Downloading WinPE ISO.", BuildId: buildId);
        public string? Iso;
        public AgentUsbVariant? Variant;
        public AgentUsbDrive[] Drives = [];
    }

    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Job? current;
    private static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    private const string Api = "api/agent/v1/admin/usb/";

    private bool Owns(Job job, string sid, int session) => job.Sid == sid && job.Session == session;

    private void Set(Job job, string state, string message, int progress = 0)
    {
        lock (gate)
            job.Status = new(
                state,
                message,
                progress,
                Variants: state == PortalToolStatus.Ready && job.Variant is { } variant ? [variant] : null,
                BuildId: job.Status.BuildId) { Architecture = job.Architecture };
    }

    public async Task<AgentUsbStatus> HandleAsync(
        string sid,
        int session,
        AgentUsbRequest request,
        CancellationToken ct)
    {
        if (request.Operation == Operation.Status)
        {
            return Status(sid, session);
        }

        if (request.Operation == Operation.Cancel)
        {
            lock (gate)
            {
                if (current is { } job && Owns(job, sid, session))
                {
                    job.Stop.Cancel();

                    return job.Status with
                    {
                        Message = "Cancelling. A partially written USB will need to be created again."
                    };
                }

                return new(PortalToolStatus.Idle, "No USB operation is running.");
            }
        }

        var lease = await admin.ToolClientAsync(sid, session, "CreateBootableUsb", ct);
        using var client = lease.Client;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Stop, shutdown.Token);

        if (request.Operation == Operation.Catalog)
        {
            var architecture = WindowsArchitecture.Require(request.Architecture);
            var variants = (await client.GetFromJsonAsync<AgentUsbVariant[]>($"{Api}variants?architecture={architecture}", linked.Token) ?? [])
                .Where(x => x.Architecture == architecture).ToArray();

            return new(
                PortalToolStatus.Catalog,
                variants.Length == 0
                    ? $"No ready {architecture} WinPE boot environments are available. Build a boot environment for this architecture on the server first."
                    : "Choose a WinPE boot environment, then download its ISO.",
                Variants: variants) { Architecture = architecture };
        }

        if (request.Operation == Operation.Download)
        {
            if (request.BuildId is null || request.BuildId == Guid.Empty)
                throw new InvalidOperationException("Select a WinPE boot environment.");

            var architecture = WindowsArchitecture.Require(request.Architecture);
            Job job;

            lock (gate)
            {
                if (current is not null)
                    return new(
                        PortalToolStatus.Busy,
                        "Another USB download or operation is active. Finish or cancel it first.");

                job = new(
                    sid,
                    session,
                    request.BuildId.Value,
                    CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token)) { Architecture = architecture };
                job.Status = job.Status with { Architecture = architecture };
                job.Stop.CancelAfter(TimeSpan.FromHours(24));
                current = job;
            }

            _ = Task.Run(() => DownloadAsync(job));

            return job.Status;
        }

        Job ready;

        lock (gate)
        {
            ready = current is { } job && Owns( job, sid, session)
                    && job.Status.Status == PortalToolStatus.Ready
                    && !job.Stop.IsCancellationRequested
                ? job
                : throw new InvalidOperationException("Download a WinPE ISO before selecting a drive.");
        }

        if (request.Operation == Operation.Drives)
        {
            var output = await PowerShellAsync(
                $"{Script}\nConvertTo-Json -InputObject @(Get-UsbTargets) -Compress",
                linked.Token);
            var drives = JsonSerializer.Deserialize<AgentUsbDrive[]>(output, json) ?? [];

            lock (gate)
            {
                if (current != ready || ready.Status.Status != PortalToolStatus.Ready
                    || ready.Stop.IsCancellationRequested)
                    throw new InvalidOperationException("The USB operation ended.");

                ready.Drives = drives;

                return ready.Status with
                {
                    Drives = drives,
                    Message = $"{ready.Variant!.DisplayName}. {(drives.Length == 0
                        ? "No eligible external USB/SD drives found. Connect a drive and refresh."
                        : "Select the external disk to erase and make bootable.")}"
                };
            }
        }

        if (request.Operation == Operation.Write)
        {
            lock (gate)
            {
                if (!request.ConfirmErase || request.Drive is null || !ready.Drives.Contains(request.Drive)
                    || ready.Status.Status != PortalToolStatus.Ready
                    || ready.Stop.IsCancellationRequested)
                    throw new InvalidOperationException("Refresh drives, select a disk, and confirm erasing it.");

                if (!AgentExecutionGate.Gate.Wait(0))
                    return new(
                        PortalToolStatus.Busy,
                        "The agent is busy with another operation. Try again when it finishes.");

                Set(
                    ready,
                    PortalToolStatus.Running,
                    "Preparing, copying, and verifying USB media. Do not disconnect the drive.",
                    60);
            }

            _ = Task.Run(() => WriteAsync(ready, request.Drive));

            return ready.Status;
        }

        throw new InvalidOperationException("Unknown USB operation.");
    }

    private async Task DownloadAsync(Job job)
    {
        var retain = false;

        try
        {
            var lease = await admin.ToolClientAsync(job.Sid, job.Session, "CreateBootableUsb", job.Stop.Token);
            using var client = lease.Client;
            client.Timeout = Timeout.InfiniteTimeSpan;
            var ct = job.Stop.Token;
            var variants = await client.GetFromJsonAsync<AgentUsbVariant[]>($"{Api}variants?architecture={job.Architecture}", ct) ?? [];
            job.Variant = variants.SingleOrDefault(x => x.BuildId == job.Status.BuildId && x.Architecture == job.Architecture)
                          ?? throw new InvalidOperationException("This boot environment changed or is no longer ready. Refresh boot environments.");

            if (job.Variant.Size <= 0 || job.Variant.Sha256.Length != 64 || !job.Variant.Sha256.All(Uri.IsHexDigit))
                throw new IOException("Invalid ISO metadata.");

            var directory = Path.Combine(Path.GetFullPath(options.StateDirectory), "UsbMedia");
            Directory.CreateDirectory(directory);
            job.Iso = Path.Combine(directory, $"{Guid.NewGuid().ToString("N")}.iso");
            using var response = await AgentResponseVerification.DownloadAsync(client,
                $"{Api}builds/{job.Variant.BuildId:D}/iso?architecture={job.Architecture}",
                job.Variant.Sha256,
                ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(
                             job.Iso,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             DownloadBufferBytes,
                             true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[DownloadBufferBytes];
                long received = 0;
                int count;

                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    received += count;

                    if (received > job.Variant.Size)
                        throw new IOException("ISO download exceeded the expected size.");

                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    hash.AppendData(buffer, 0, count);
                    Set(
                        job,
                        PortalToolStatus.Running,
                        $"Downloading {job.Variant.Name} ({job.Architecture}): {received / BytesPerMiB:N0} / {job.Variant.Size / BytesPerMiB:N0} MB.",
                        (int)(received * 55 / job.Variant.Size));
                }

                if (received != job.Variant.Size
                    || !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                        job.Variant.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The downloaded ISO failed its size or SHA-256 check.");
            }

            ct.ThrowIfCancellationRequested();
            Set(
                job,
                PortalToolStatus.Ready,
                $"Downloaded and verified {job.Variant.DisplayName}. Select an external drive.",
                55);
            retain = true;
            // Retain verified media for this bounded job; writing still requires a current admin session.
            var lifetime = job.Stop.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, lifetime);
                }
                catch (OperationCanceledException)
                {
                }

                lock (gate)
                {
                    if (job.Status.Status != PortalToolStatus.Ready)
                        return;

                    Set(job, PortalToolStatus.Cancelled, "USB creation ended. Download a boot environment to start again.");
                }

                Cleanup(job);
            });
        }
        catch (Exception ex)
        {
            Failed(job, ex);
        }
        finally
        {
            if (!retain) Cleanup(job);
        }
    }

    private async Task WriteAsync(Job job, AgentUsbDrive drive)
    {
        try
        {
            var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                Iso = job.Iso,
                Architecture = job.Architecture,
                Drive = drive
            }));
            var command = $"{Script}\n$data = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{data}')) | ConvertFrom-Json\nWrite-UsbMedia $data";
            var message = await PowerShellAsync(command, job.Stop.Token);
            Set(
                job,
                PortalToolStatus.Succeeded,
                $"{message.Trim()} Safely eject the drive before removing it.",
                100);
        }
        catch (Exception ex)
        {
            Failed(job, ex);
        }
        finally
        {
            // Cancellation kills the process tree; run unmount separately as its finally block may not execute.
            if (job.Iso is { } iso)
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await PowerShellAsync(
                        $"Dismount-DiskImage -ImagePath '{iso.Replace( "'", "''")}' -ErrorAction SilentlyContinue | Out-Null",
                        deadline.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not unmount USB source ISO");
                }

            AgentExecutionGate.Gate.Release();
            Cleanup(job);
        }
    }

    private readonly Dictionary<(string, int), AgentUsbStatus> completed = [];

    public AgentUsbStatus Status(string sid, int session)
    {
        lock (gate)
            return current is { } job && Owns( job, sid, session)
                ? job.Status
                : completed.GetValueOrDefault((sid, session))
                  ?? new(PortalToolStatus.Idle, "Select a WinPE boot environment to download.");
    }

    private void Failed(Job job, Exception ex)
    {
        logger.LogWarning(ex, "USB media operation failed");
        Set(
            job,
            ex is OperationCanceledException ? PortalToolStatus.Cancelled : PortalToolStatus.Failed,
            ex is OperationCanceledException
                ? "USB creation cancelled or administrator access ended. Partially written media must be created again."
                : $"USB creation failed: {ex.Message}");
    }

    private void Cleanup(Job job)
    {
        job.Stop.Cancel();

        if (job.Iso is { } iso)
            try
            {
                File.Delete(iso);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove temporary USB ISO");
            }

        lock (gate)
        {
            if (completed.Count >= 64)
                completed.Clear();

            completed[(job.Sid, job.Session)] = job.Status;

            if (current == job)
                current = null;
        }

        job.Stop.Dispose();
    }

    private static string Script
    {
        get
        {
            using var stream = typeof(AgentUsbTool).Assembly.GetManifestResourceStream("CloneDeploy_Agent.UsbMedia.ps1")!;
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
    }

    internal static async Task<string> PowerShellAsync(string script, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes($"[Console]::OutputEncoding = [Text.Encoding]::UTF8\n$ErrorActionPreference = 'Stop'\ntry {{\n{script}\n}} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}")));
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();

        if (!process.Start())
            throw new IOException("Could not start Windows storage tools.");

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            try
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var result = await output;
        var detail = await error;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(detail.Trim());

        return result;
    }

    public void Dispose()
    {
        shutdown.Cancel();
        shutdown.Dispose();
    }
}
