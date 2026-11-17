using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

public sealed class AgentDriverPackTool(
    AgentOptions options,
    AgentAdminSession admin,
    IPolicyProcess process,
    ILogger<AgentDriverPackTool> logger) : IDisposable
{
    private const int MaximumPackNameCharacters = 200;
    private const int MaximumFailureDetailCharacters = 500;

    private const int MaximumUploadChunkBytes = 1024 * 1024;

    private sealed class Job
    {
        public AgentDriverPackStatus Status = new(PortalToolStatus.Running, "Exporting drivers from this computer.");
        public CancellationTokenSource Stop = null!;
    }

    private readonly object gate = new();
    private readonly Dictionary<(string Sid, int Session), Job> jobs = [];

    private readonly CancellationTokenSource shutdown = new();

    // Test fixtures supply hardware without executing WMI or DISM on the developer's computer.
    internal Func<(string Manufacturer, string Model)> Hardware { get; set; } = ReadHardware;

    private static (string Manufacturer, string Model) ReadHardware()
    {
        using var query = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        using var results = query.Get();

        foreach (ManagementObject row in results)
            using (row)
                return (Convert.ToString(row["Manufacturer"])?.Trim() ?? "", Convert.ToString(row["Model"])?.Trim() ?? "");

        throw new InvalidOperationException("The computer's manufacturer and model could not be read.");
    }

    public AgentDriverPackStatus Status(string sid, int sessionId)
    {
        lock (gate)
            return jobs.TryGetValue((sid, sessionId), out var job)
                ? job.Status
                : new(
                    PortalToolStatus.Idle,
                    "Export installed third-party drivers and upload them as a new driver pack.");
    }

    public async Task<AgentDriverPackStatus> StartAsync(
        string sid,
        int sessionId,
        AgentDriverPackRequest request,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumPackNameCharacters || name.Any(char.IsControl))
            return new(PortalToolStatus.Failed, "Enter a pack name between 1 and 200 characters.");

        var lease = await admin.ToolClientAsync(sid, sessionId, "CreateDriverPack", ct);

        if (!AgentExecutionGate.Gate.Wait(0))
        {
            lease.Client.Dispose();

            return new(PortalToolStatus.Busy, "The agent is busy with another operation. Try again when it finishes.");
        }

        Job? job = null;

        try
        {
            job = new Job { Stop = CancellationTokenSource.CreateLinkedTokenSource(lease.Stop, shutdown.Token) };

            lock (gate)
                jobs[(sid, sessionId)] = job;

            _ = Task.Run(() => RunAsync(job, lease.Client, name));

            return job.Status;
        }
        catch
        {
            job?.Stop.Dispose();
            lease.Client.Dispose();
            AgentExecutionGate.Gate.Release();
            throw;
        }
    }

    public AgentDriverPackStatus Cancel(string sid, int sessionId)
    {
        lock (gate)
        {
            if (jobs.TryGetValue((sid, sessionId), out var job) && job.Status.Status == PortalToolStatus.Running)
            {
                job.Stop.Cancel();

                return job.Status with
                {
                    Message = "Cancelling driver collection."
                };
            }
        }

        return Status(sid, sessionId);
    }

    private void Update(Job job, string message, int progress)
    {
        lock (gate)
            job.Status = new(PortalToolStatus.Running, message, progress);
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Administrator access ended. Sign in again and restart the driver collection.");

        string? detail = null;

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            if (problem.TryGetProperty("detail", out var value))
                detail = value.GetString();
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(detail is { Length: > 0 and <= MaximumFailureDetailCharacters }
            ? detail
            : $"The server rejected the upload (HTTP {(int)response.StatusCode}).");
    }

    private async Task RunAsync(Job job, HttpClient client, string name)
    {
        const string api = "api/agent/v1/admin/driver-packs";
        string? root = null, work = null;
        Guid? upload = null;

        try
        {
            root = Path.GetFullPath(Path.Combine(options.StateDirectory, "DriverExports"));
            work = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var ct = job.Stop.Token;
            ct.ThrowIfCancellationRequested();
            var hardware = Hardware();

            if (string.IsNullOrWhiteSpace(hardware.Manufacturer) || string.IsNullOrWhiteSpace(hardware.Model))
                throw new InvalidOperationException("Manufacturer and model are required to create the pack.");

            var exported = Path.Combine(work, "drivers");
            Directory.CreateDirectory(exported);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/Online");
            start.ArgumentList.Add("/Export-Driver");
            start.ArgumentList.Add($"/Destination:{exported}");
            start.ArgumentList.Add($"/LogPath:{Path.Combine(work, "dism.log")}");
            var result = await process.RunAsync(start, 10, ct);

            if (result.ExitCode != 0)
            {
                logger.LogWarning($"DISM driver export failed with exit code {result.ExitCode}: {result.Error.Trim()}");
                logger.LogDebug($"DISM export output: {result.Output}");
                throw new InvalidOperationException($"Driver export failed (DISM exit code {result.ExitCode}). See the agent log for details.");
            }

            var files = Directory.GetFiles(exported, "*", SearchOption.AllDirectories);

            if (!files.Any(f => f.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("No third-party driver packages were exported from this computer.");

            Update(job, "Packaging exported drivers.", 20);
            var archivePath = Path.Combine(work, "drivers.zip");
            await using (var output = File.Create(archivePath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(
                        Path.GetRelativePath(exported, file).Replace('\\', '/'),
                        CompressionLevel.Fastest);
                    await using var input = File.OpenRead(file);
                    await using var target = entry.Open();
                    await input.CopyToAsync(target, ct);
                }

            Update(job, "Uploading driver pack.", 30);
            await using var zip = File.OpenRead(archivePath);
            using var begun = await client.PostAsJsonAsync(
                $"{api}/uploads",
                new AgentDriverUploadRequest("drivers.zip", zip.Length),
                ct);
            await RequireSuccessAsync(begun, ct);
            var session = await begun.Content.ReadFromJsonAsync<AgentDriverUploadSession>(ct)
                          ?? throw new IOException("Invalid upload session.");
            upload = session.Id;

            if (session.ChunkSize is < 1 or > MaximumUploadChunkBytes)
                throw new IOException("Invalid upload chunk size.");

            var buffer = new byte[session.ChunkSize];
            long offset = 0;

            while (offset < zip.Length)
            {
                var length = (int)Math.Min(buffer.Length, zip.Length - offset);
                await zip.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
                using var body = new ByteArrayContent(buffer, 0, length);
                using var sent = await client.PutAsync($"{api}/uploads/{upload:D}/chunks?offset={offset}", body, ct);
                await RequireSuccessAsync(sent, ct);
                offset += length;
                Update(
                    job,
                    $"Uploading driver pack ({offset * 100 / zip.Length}%).",
                    30 + (int)(offset * 60 / zip.Length));
            }

            using var complete = await client.PostAsync($"{api}/uploads/{upload:D}/complete", null, ct);
            await RequireSuccessAsync(complete, ct);
            Update(job, "Creating driver pack on the server.", 95);
            var pack = await PublishAsync(
                client,
                api,
                new(upload.Value, name, hardware.Manufacturer, hardware.Model),
                ct);

            lock (gate)
                job.Status = new(
                    PortalToolStatus.Succeeded,
                    $"Created '{pack.Name}'. It is available under Driver packs in the web UI.",
                    100,
                    pack.Id,
                    pack.Name);

            logger.LogInformation($"Created driver pack {pack.Id} from this computer");
            upload = null;
        }
        catch (OperationCanceledException)
        {
            lock (gate)
                job.Status = new(
                    PortalToolStatus.Cancelled,
                    "Driver collection ended because it was cancelled, timed out, or administrator access ended.");
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Driver pack collection failed: {ex.Message}");
            logger.LogDebug(ex, "Driver pack collection detail");

            lock (gate)
                job.Status = new(
                    PortalToolStatus.Failed,
                    ex is UnauthorizedAccessException or InvalidOperationException
                        ? ex.Message
                        : "Driver collection failed. Check the agent log and server connection, then try again.");
        }
        finally
        {
            try
            {
                if (upload.HasValue)
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var cancel = await client.DeleteAsync($"{api}/uploads/{upload:D}", timeout.Token);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                    {
                    }

                try
                {
                    if (work is not null)
                    {
                        if (!work.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Invalid export cleanup path.");

                        if (Directory.Exists(work))
                            Directory.Delete(work, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning($"Could not remove temporary driver export: {ex.Message}");
                }
            }
            finally
            {
                client.Dispose();

                lock (gate)
                    job.Stop.Dispose();

                AgentExecutionGate.Gate.Release();
            }
        }
    }

    private static async Task<AgentDriverPackCreated> PublishAsync(
        HttpClient client,
        string api,
        AgentDriverPackCreateRequest request,
        CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
            try
            {
                using var response = await client.PostAsJsonAsync(api, request, ct);
                await RequireSuccessAsync(response, ct);

                return await response.Content.ReadFromJsonAsync<AgentDriverPackCreated>(ct)
                       ?? throw new IOException("Invalid driver pack response.");
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested
                                       && ex is HttpRequestException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            } // Publication is idempotent for this upload/session.
    }

    public void Dispose()
    {
        shutdown.Cancel();
    }
}