using System.Diagnostics;
using System.Net.Http.Json;
using System.Management;
using System.Runtime.InteropServices;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

public sealed class AgentImagePrepTool(
    AgentOptions options,
    AgentAdminSession admin,
    IPolicyProcess process,
    ILogger<AgentImagePrepTool> logger) : IDisposable
{
    public AgentImagingBoot? ImagingBoot { get; init; }
    private const string Api = "api/agent/v1/admin/image-prep";
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private AgentImagePrepStatus status = new(PortalToolStatus.Idle, "Select a profile to prepare this computer for capture.");
    private bool running;
    private bool disposed;

    private AgentImagePrepRequest WithCurrentHardware(AgentImagePrepRequest request)
    {
        string? manufacturer = null, model = null;
        try
        {
            using var search = new ManagementObjectSearcher(new ManagementScope("root\\cimv2"),
                new ObjectQuery("SELECT Manufacturer, Model FROM Win32_ComputerSystem"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
            using var results = search.Get();
            foreach (ManagementObject computer in results)
            {
                using (computer)
                {
                    manufacturer = (computer["Manufacturer"] as string)?.Trim();
                    model = (computer["Model"] as string)?.Trim();
                }
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the current computer model; image prep will use the default WinPE environment");
        }
        return request with { NativeArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Manufacturer = manufacturer, Model = model };
    }

    public async Task<AgentImagePrepStatus> HandleAsync(
        string sid, int sessionId, AgentImagePrepRequest request, CancellationToken ct)
    {
        if (request.Operation == AgentImagePrepOperation.Status)
        {
            lock (gate)
                return status;
        }

        var lease = await admin.ToolClientAsync(sid, sessionId, "RunImagePrep", ct);
        using var client = lease.Client;
        using var authorized = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Stop);

        if (request.Operation == AgentImagePrepOperation.Profiles)
        {
            var profiles = await client.GetFromJsonAsync<AgentImagePrepProfile[]>(Api, authorized.Token) ?? [];
            var images = profiles.Any(x => x.RebootAndCapture)
                ? await client.GetFromJsonAsync<AgentCaptureImage[]>($"{Api}/images", authorized.Token) ?? [] : [];
            lock (gate)
                return status with { Profiles = profiles, Images = images };
        }

        if (request.Operation != AgentImagePrepOperation.Start || request.ProfileId <= 0)
            throw new InvalidOperationException("Choose an image prep profile.");

        if (!AgentExecutionGate.Gate.Wait(0))
            return new(PortalToolStatus.Busy, "The agent is busy with another operation. Try again when it finishes.");

        AgentImagePrepPlan? admitted = null;
        try
        {
            client.DefaultRequestHeaders.Add("X-CloneDeploy-ImagePrep-Uninstall", "1");
            // Replace any UI-supplied hardware with values read by the agent service.
            request = WithCurrentHardware(request);
            using var response = await client.PostAsJsonAsync($"{Api}/{request.ProfileId}/start", request, authorized.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(authorized.Token);
                throw new InvalidOperationException(error.TryGetProperty("message", out var message) ? message.GetString() : "Image prep authorization failed.");
            }
            var plan = await response.Content.ReadFromJsonAsync<AgentImagePrepPlan>(authorized.Token)
                ?? throw new IOException("Invalid image prep profile response.");
            admitted = plan;
            if (plan.RebootAndCapture && (plan.Capture is null || ImagingBoot is null))
                throw new InvalidOperationException("Automatic capture is unavailable. Update the agent.");
            ImagePrepRunner.Validate(plan);
            if (plan.UninstallAgent) _ = ImagePrepUninstall.FindProduct(AppContext.BaseDirectory);
            if (plan.ResetAgent && string.IsNullOrEmpty(options.ProvisioningKey))
                throw new InvalidOperationException("A saved provisioning key is required to reset the agent for imaging.");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.exe")))
                throw new InvalidOperationException("Image prep must run from the installed agent service.");
            authorized.Token.ThrowIfCancellationRequested();

            lock (gate)
            {
                running = true;
                status = new(PortalToolStatus.Running, $"Preparing this computer with '{plan.Name}'.");
            }

            // Once explicitly started, preparation survives UI closure, session lock, and sign-in expiry.
            _ = Task.Run(() => RunAsync(plan));
            return status;
        }
        catch
        {
            if (admitted?.Capture is { } capture && ImagingBoot is not null)
                try { await ImagingBoot.AbortCaptureAsync(capture.UploadId); }
                catch (Exception ex) { logger.LogWarning("Could not cancel capture: {Message}", ex.Message); }
            AgentExecutionGate.Gate.Release();
            throw;
        }
    }

    private async Task RunAsync(AgentImagePrepPlan plan)
    {
        var finalizing = false;
        var progressLog = AgentDiagnosticLogs.FinalizationLog(options.StateDirectory);
        try
        {
            AgentDiagnosticLogs.Prepare(options.StateDirectory);
            File.AppendAllText(progressLog, $"{DateTimeOffset.UtcNow:O} Image preparation started.\r\n");
            if (plan.Capture is { } capture)
                await ImagingBoot!.StageCaptureAsync(capture, message =>
                {
                    lock (gate) status = new(PortalToolStatus.Running, message);
                }, stop.Token);
            var runner = new ImagePrepRunner(options, process);
            await runner.RunAsync(plan, (message, progress) =>
            {
                logger.LogInformation($"Image prep: {message}");
                File.AppendAllText(progressLog, $"{DateTimeOffset.UtcNow:O} {message}\r\n");
                lock (gate)
                    status = new(PortalToolStatus.Running, message, progress);
            }, stop.Token);

            if (plan.Capture is { } prepared)
                await ImagingBoot!.CaptureReadyAsync(prepared, stop.Token);

            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(plan.UninstallAgent ? "--finalize-image-uninstall"
                : plan.Capture is null ? "--finalize-image-prep" : "--finalize-image-capture");
            start.ArgumentList.Add(Path.GetFullPath(options.StateDirectory));
            start.ArgumentList.Add(plan.UninstallAgent ? plan.RebootAndCapture ? "capture" : "shutdown" : plan.ResetAgent ? "reset" : "keep");
            using var helper = Process.Start(start) ?? throw new IOException("Could not start image prep finalization.");
            finalizing = true;
            lock (gate)
                status = new("Finalizing", plan.Capture is null
                    ? "Finalizing preparation. The agent will stop before the final shutdown step. If Sysprep is enabled, it will shut down Windows when complete."
                    : "Finalizing preparation. The agent will stop before the final restart into WinPE. If Sysprep is enabled, it will restart Windows when complete. Keep Ethernet connected.", 100);

            await helper.WaitForExitAsync(stop.Token);
            if (helper.ExitCode != 0)
            {
                finalizing = false;
                throw new InvalidOperationException($"Image prep finalization failed. Check {progressLog} before capture.");
            }
        }
        catch (OperationCanceledException) when (finalizing)
        {
            // The helper stops this service before clearing its state and shutting down Windows.
        }
        catch (Exception ex)
        {
            if (plan.Capture is { } capture)
                try { await ImagingBoot!.AbortCaptureAsync(capture.UploadId); }
                catch (Exception cleanup) { logger.LogError("Capture cleanup failed: {Message}", cleanup.Message); }
            logger.LogError($"Image prep failed: {ex.Message}");
            try { File.AppendAllText(progressLog, $"{DateTimeOffset.UtcNow:O} Image prep failed: {ex.Message}\r\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            lock (gate)
                status = new(PortalToolStatus.Failed, ex is InvalidOperationException ? ex.Message
                    : "Image prep failed. The computer was not shut down. Check the agent log before trying again.");
        }
        finally
        {
            lock (gate)
            {
                running = false;
                if (disposed)
                    stop.Dispose();
            }
            if (!finalizing)
                AgentExecutionGate.Gate.Release();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            stop.Cancel();
            if (!running)
                stop.Dispose();
        }
    }
}
