using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal static class ImagePrepFinalizer
{
    internal static Task<int> RunAsync(string directory, bool reset) => RunCoreAsync(directory, reset, false, false);
    internal static Task<int> RunCaptureAsync(string directory, bool reset) => RunCoreAsync(directory, reset, true, false);

    internal static Task<int> RunUninstallAsync(string directory, bool capture) => RunCoreAsync(directory, false, capture, true);

    private static async Task<int> RunCoreAsync(string directory, bool reset, bool capture, bool uninstall)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
            throw new UnauthorizedAccessException("Image prep finalization requires the agent service identity.");

        directory = ValidateStateDirectory(directory);
        var product = uninstall ? ImagePrepUninstall.FindProduct(AppContext.BaseDirectory) : (Guid?)null;
        AgentDiagnosticLogs.Prepare(directory);
        var log = AgentDiagnosticLogs.FinalizationLog(directory);
        using var service = new ServiceController(AgentOptions.WindowsServiceName);
        var resetStarted = false;
        var sysprepStarted = false;
        HttpClient? captureClient = null;
        X509Certificate2? captureCertificate = null;
        Guid? captureId = null;
        try
        {
            var powerAction = ImagePrepPowerAction.Load(directory);
            await Task.Delay(TimeSpan.FromSeconds(5));
            File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} Stopping the agent for image capture.\r\n");
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));

            // SCM can report Stopped before the host has disposed its database connections.
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            AgentStore store;
            while (true)
            {
                try
                {
                    store = new AgentStore(directory);
                    break;
                }
                catch (IOException) when (!deadline.IsCancellationRequested)
                {
                    await Task.Delay(500, deadline.Token);
                }
            }

            using (store)
            {
                if (capture)
                {
                    var path = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, PreparedCaptureHandoff.DirectoryName, PreparedCaptureHandoff.FileName);
                    var handoff = JsonSerializer.Deserialize<PreparedCaptureHandoff>(await File.ReadAllTextAsync(path))
                        ?? throw new InvalidOperationException("Capture handoff is missing.");
                    captureId = handoff.UploadId;
                    var options = new AgentOptions { ServerUrl = store.GetSetting("ServerUrl") ?? "",
                        ServerCaCertificatePath = store.GetSetting("ServerCaCertificatePath") };
                    var handler = new HttpClientHandler { AllowAutoRedirect = false };
                    AgentServerTrust.Configure(handler, options);
                    // The capture token alone does not admit installed agents to the imaging API.
                    // Open the existing key without constructing/re-enrolling an agent identity.
                    var identityState = store.LoadIdentity()
                        ?? throw new InvalidOperationException("Capture finalization requires an enrolled agent identity.");
                    if (string.IsNullOrWhiteSpace(identityState.CertificatePem)
                        || !identityState.KeyName.StartsWith("CloneDeploy.Agent.", StringComparison.Ordinal)
                        || !CngKey.Exists(identityState.KeyName))
                        throw new InvalidOperationException("Capture finalization requires the existing agent certificate and private key.");
                    using (var key = CngKey.Open(identityState.KeyName))
                    using (var rsa = new RSACng(key))
                    using (var certificate = X509Certificate2.CreateFromPem(identityState.CertificatePem))
                        captureCertificate = certificate.CopyWithPrivateKey(rsa);
                    // AgentResponseVerification uses this certificate for fresh signed request proofs as well as TLS.
                    handler.ClientCertificates.Add(captureCertificate);
                    captureClient = new HttpClient(new AgentResponseVerification(handler, options)) { BaseAddress = options.GetServerUri(), Timeout = TimeSpan.FromSeconds(20) };
                    captureClient.DefaultRequestHeaders.Add("X-CloneDeploy-Capture", handoff.Token);
                    using var available = await captureClient.GetAsync($"api/imaging/prepared-capture/uploads/{captureId}");
                    available.EnsureSuccessStatusCode(); // Recheck cancellation before clearing the Windows identity.
                }
                if (reset || uninstall)
                {
                    if (reset) ImagePrepEnrollment.Save(store, directory);
                    else File.Delete(Path.Combine(directory, "image-enrollment.json"));
                    resetStarted = true;
                    store.PrepareForImage();
                    ClearHistory(directory);
                }
            }

            if (uninstall)
            {
                await ImagePrepUninstall.LaunchAsync(directory, product!.Value, capture);
                File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} Uninstall helper started; it will shut down or restart only after removal succeeds.\r\n");
                return 0; // Release this executable before Windows Installer removes it.
            }
            if (capture) await AgentImagingBoot.ArmCaptureAsync(directory, CancellationToken.None);
            File.Delete(ImagePrepPowerAction.RequestPath(directory));
            if (powerAction.RunSysprep)
            {
                sysprepStarted = true;
                await powerAction.ExecuteAsync(capture, log);
                return 0; // Sysprep owns shutdown/reboot; no agent work follows generalization.
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { capture ? "/r" : "/s", "/t", "30", "/c", capture
                ? "CloneDeploy image preparation completed. Restarting into WinPE for capture."
                : "CloneDeploy image preparation completed. Boot into your capture environment next." })
                start.ArgumentList.Add(argument);
            using var shutdown = Process.Start(start) ?? throw new IOException("Could not schedule shutdown.");
            await shutdown.WaitForExitAsync();
            if (shutdown.ExitCode != 0)
                throw new InvalidOperationException($"Shutdown returned exit code {shutdown.ExitCode}.");

            File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} Image prep complete. Shutdown scheduled.\r\n");
            return 0;
        }
        catch (Exception ex)
        {
            if (captureClient is not null && captureId is not null)
                try
                {
                    using var response = await captureClient.DeleteAsync($"api/imaging/prepared-capture/uploads/{captureId}");
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception report) { File.AppendAllText(log, $"Could not report failed capture: {report.Message}\r\n"); }
            if (capture)
                try { await AgentImagingBoot.DisarmCaptureAsync(directory, CancellationToken.None); }
                catch (Exception cleanup) { File.AppendAllText(log, $"Capture boot cleanup failed: {cleanup.Message}\r\n"); }
            File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} Finalization failed: {ex.Message}. Do not capture until corrected.\r\n");
            // Do not allow the reference computer to enroll again after its identity was cleared.
            if (!resetStarted && !sysprepStarted)
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped)
                    service.Start();
            }
            return 1;
        }
        finally { captureClient?.Dispose(); captureCertificate?.Dispose(); }
    }

    internal static string ValidateStateDirectory(string directory)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Agent");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Image prep finalization requires the installed agent state directory.");
        // A matching spelling must not redirect privileged file operations elsewhere.
        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Image prep state must not use redirected directories.");
        return fullPath;
    }

    internal static void ClearHistory(string directory)
    {
        foreach (var name in new[] { "policies", "user-logins", "device-tasks", "portal-messages", "run-as" })
            foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm" })
                File.Delete(Path.Combine(directory, $"{name}{suffix}"));
    }
}
