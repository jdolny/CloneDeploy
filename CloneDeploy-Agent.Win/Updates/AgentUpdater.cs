using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using CloneDeploy_Agent.Contracts;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace CloneDeploy_Agent;

/// <summary>Stages authenticated releases and hands installation to an independent process that survives service replacement.</summary>
public sealed class AgentUpdater(AgentOptions options, AgentIdentity identity, ILogger<AgentUpdater> logger)
{
    private const int DownloadBufferBytes = 65536;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private string Folder => Path.Combine(Path.GetFullPath(options.StateDirectory), "Updates");

    public async Task CheckAsync(AgentUpdateOffer? release, CancellationToken ct)
    {
        // update-result.json is diagnostic output only. A previous MSI result must never
        // block check-in, version comparison, or another attempt (even if malformed/locked).
        if (release is null || release.Architecture != WindowsArchitecture.Process || !release.IsNewerThan(typeof(AgentUpdater).Assembly.GetName().Version!.ToString()))
            return;

        using var account = WindowsIdentity.GetCurrent();

        if (!WindowsServiceHelpers.IsWindowsService() || !account.IsSystem || options.RunOnce)
        {
            logger.LogInformation(
                $"Agent {release.Version} is available; automatic upgrades run only in the installed SYSTEM service");

            return;
        }

        AgentStatePermissions.Protect(Folder);
        // A prior release's attempt must not delay a newly prepared package.
        var cooldown = Path.Combine(Folder, $"retry-after-{release.Sha256.ToUpperInvariant()}.txt");

        if (File.Exists(cooldown)
            && DateTimeOffset.TryParse(
                await File.ReadAllTextAsync(cooldown, ct),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var retryAfter)
            && retryAfter > DateTimeOffset.UtcNow)
        {
            logger.LogInformation(
                $"Agent {release.Version} upgrade is waiting until {retryAfter.UtcDateTime} UTC after a previous attempt for this package");

            return;
        }

        // Do not interrupt an executing policy or task, including another MSI installer.
        if (!await AgentExecutionGate.Gate.WaitAsync(0, ct))
        {
            logger.LogInformation("Agent upgrade deferred until policy and task execution is idle");

            return;
        }

        try
        {
            await File.WriteAllTextAsync(cooldown, DateTimeOffset.UtcNow.AddHours(1).ToString("O"), ct);
            var package = Path.Combine(Folder, $"{release.Sha256.ToUpperInvariant()}.msi");
            var partial = $"{package}.part";

            try
            {
                using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadTimeout.CancelAfter(DownloadTimeout);
                var downloadToken = downloadTimeout.Token;
                using var certificate = identity.GetClientCertificate()
                                        ?? throw new IOException("Agent certificate is unavailable.");
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                AgentServerTrust.Configure(handler, options);
                handler.ClientCertificates.Add(certificate);
                using var client = new HttpClient(new AgentResponseVerification(handler, options))
                {
                    BaseAddress = options.GetServerUri(),
                    Timeout = DownloadTimeout
                };
                logger.LogInformation($"Downloading agent release {release.Version}");
                using var response = await AgentResponseVerification.DownloadAsync(client,
                    $"api/agent/v1/updates/{release.Sha256}?architecture={release.Architecture}",
                    release.Sha256,
                    downloadToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(downloadToken))
                await using (var destination = new FileStream(
                                 partial,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 DownloadBufferBytes,
                                 true))
                    await CopyVerifiedAsync(source, destination, release, downloadToken);
                ValidatePackage(partial, release.Version);
                File.Move(partial, package, true);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }

            var script = Path.Combine(Folder, "Install-AgentUpdate.ps1");
            await using (var verifier = typeof(AgentUpdater).Assembly.GetManifestResourceStream("CloneDeploy_Agent.VerifyAgentPublisher.ps1")!)
            await using (var output = new FileStream(Path.Combine(Folder, "Verify-AgentPublisher.ps1"), FileMode.Create, FileAccess.Write, FileShare.None))
                await verifier.CopyToAsync(output, ct);
            await using (var resource = typeof(AgentUpdater).Assembly.GetManifestResourceStream("CloneDeploy_Agent.InstallAgentUpdate.ps1")!)
            await using (var output = new FileStream(script, FileMode.Create, FileAccess.Write, FileShare.None))
                await resource.CopyToAsync(output, ct);
            var start = new ProcessStartInfo(Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Folder
            };

            foreach (var argument in new[]
                     {
                         "-NoProfile",
                         "-NonInteractive",
                         "-ExecutionPolicy",
                         "Bypass",
                         "-File",
                         script,
                         "-Package",
                         package,
                         "-ExpectedHash",
                         release.Sha256,
                         "-TargetVersion",
                         release.Version,
                         "-PublisherPath",
                         Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.exe"),
                         "-LogDirectory",
                         Folder
                     })
                start.ArgumentList.Add(argument);

            using var installer = Process.Start(start) ?? throw new IOException("Could not start the agent updater.");
            logger.LogInformation($"Installing agent {release.Version}");
            // The detached PowerShell process remains alive when MSI stops this service.
            // Holding the gate prevents new tasks until shutdown or installer failure.
            await installer.WaitForExitAsync(ct);
            logger.LogWarning(
                $"Agent updater finished with code {installer.ExitCode}; see Updates/update-result.json and install.log");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent upgrade failed; retrying no sooner than one hour");
        }
        finally
        {
            AgentExecutionGate.Gate.Release();
        }
    }

    public static async Task CopyVerifiedAsync(
        Stream source,
        Stream destination,
        AgentUpdateOffer release,
        CancellationToken ct)
    {
        release.Validate();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DownloadBufferBytes];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;

            if (total > release.Length)
                throw new InvalidDataException("Agent package exceeds its advertised size.");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total != release.Length
            || !CryptographicOperations.FixedTimeEquals(
                hash.GetHashAndReset(),
                Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("Agent package size or SHA-256 does not match the prepared release.");
    }

    private static void ValidatePackage(string path, string expectedVersion)
    {
        dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", true)!)!;

        try
        {
            dynamic db = installer.OpenDatabase(path, 0);

            try
            {
                string Read(string property)
                {
                    dynamic view = db.OpenView($"SELECT `Value` FROM `Property` WHERE `Property` = '{property}'");

                    try
                    {
                        view.Execute();
                        dynamic record = view.Fetch();

                        if (record is null)
                            throw new InvalidDataException("Missing agent MSI metadata.");

                        try
                        {
                            return (string)record.StringData[1];
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(record);
                        }
                    }
                    finally
                    {
                        view.Close();
                        Marshal.FinalReleaseComObject(view);
                    }
                }

                dynamic summary = db.SummaryInformation(0);
                try
                {
                    var expectedPlatform = WindowsArchitecture.Process == "arm64" ? "Arm64" : "x64";
                    if (((string)summary.Property[7]).Split(';')[0] != expectedPlatform)
                        throw new InvalidDataException("The downloaded MSI has an unexpected architecture.");
                }
                finally { Marshal.FinalReleaseComObject(summary); }
                if (Read("UpgradeCode") != WindowsArchitecture.UpgradeCode(WindowsArchitecture.Process)
                    ||
                    AgentUpdateOffer.ParseVersion(Read("ProductVersion")) != AgentUpdateOffer.ParseVersion(expectedVersion))
                    throw new InvalidDataException("The downloaded MSI has an unexpected product or version.");
            }
            finally
            {
                Marshal.FinalReleaseComObject(db);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(installer);
        }
    }
}
