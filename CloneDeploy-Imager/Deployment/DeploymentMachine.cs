using System.Text.Json;
using System.Security.Cryptography;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.FfuFileSystem.Reading;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Deployment;

/// <summary>
/// Defines preparation and image application operations independently of server task coordination.
/// </summary>
internal interface IDeploymentMachine : IAsyncDisposable
{
    bool AgentInstalled { get; }
    Task PrepareAsync(DeploymentExecution image, Func<Stream, CancellationToken, Task> download, Action<DeploymentProgress> progress, CancellationToken ct);
    Task ApplyAsync(DeploymentExecution image, Func<Stream, CancellationToken, Task> download, Action<DeploymentProgress> progress, CancellationToken ct);
}

/// <summary>
/// Prepares the selected disk and applies WIM or FFU images using the configured tools and transport.
/// </summary>
internal sealed class DeploymentMachine(DeploymentDisk disk, ImagerSettings settings, DiagnosticLog log) : IDeploymentMachine
{
    public bool AgentInitiated { get; init; }
    public Func<CloneDeploy_Agent.Contracts.ImagingOuRequest, Action<DeploymentProgress>, CancellationToken, Task<string?>>? ResolveOu { get; init; }
    public bool AgentInstalled { get; private set; }
    private string? workspace;
    private FfuDeploymentSource? ffuSource;
    private HttpsFfuReader? httpsReader;
    private IDisposable? wimConnection;
    private FileStream? wimFile;
    private string? wimPath;
    public Func<DeploymentExecution, long, int, CancellationToken, Task<byte[]>>? ReadFfuRange { get; init; }
    public Func<DeploymentDriverArchive, string, CancellationToken, Task>? DownloadDriver { get; init; }
    public Func<string, CancellationToken, Task<Dictionary<string, string>>>? ResolveSysprepCredentials { get; init; }
    private bool uefi;
    private char windowsLetter, systemLetter, recoveryLetter;
    private readonly DeploymentTools tools = new(log);
    private static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);
    private static string PowerShell => SystemTool(@"WindowsPowerShell\v1.0\powershell.exe");

    public async Task PrepareAsync(DeploymentExecution image, Func<Stream, CancellationToken, Task> download, Action<DeploymentProgress> progress, CancellationToken ct)
    {
        if (AgentInitiated && image.Format == "Ffu")
        {
            const string delayMessage = "Agent-initiated FFU deployment: waiting 120 seconds before disk preparation. Press Ctrl+C to cancel.";
            progress(new(DeploymentStage.Preparing, null) { Status = delayMessage });
            log.Write(delayMessage);
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
            log.Write("Agent FFU deployment startup delay completed.");
        }
        progress(new(DeploymentStage.Preparing, null) { Status = "Checking target disk and deployment tools" });
        DeploymentDisks.Revalidate(disk, ct);
        if (image.SetComputerName) OfflineComputerName.Validate(image.ComputerName);
        log.Write("Deployment image integrity verification: " + (image.Performance.VerifyImage ? "enabled" : "disabled by server setting"));
        if (image.Multicast is { } multicast)
        {
            if (image.Format != "Wim" || image.WimTransport != "Multicast" || multicast.Port is < MulticastPortLimits.MinimumReceiverPort or > MulticastPortLimits.MaximumReceiverPort || multicast.Port % 2 != 0 ||
                !File.Exists(ImagingClient.MulticastReceiverExecutable))
                throw new InvalidOperationException("Multicast metadata or the native UDPcast receiver is missing. Rebuild WinPE with the current Imager.");
            progress(new(DeploymentStage.Preparing, null) { Status = "Preparing the WinPE multicast receiver" });
            await tools.RunAsync(SystemTool("netsh.exe"), ["advfirewall", "firewall", "add", "rule", "name=CloneDeploy Multicast " + multicast.SessionId,
                "dir=in", "action=allow", "protocol=UDP", $"localport={multicast.Port}-{multicast.Port + 1}",
                "program=" + ImagingClient.MulticastReceiverExecutable, "profile=any"], ct);
        }
        uefi = DeploymentDisks.Uefi();
        if (image.Format == "Ffu" && !uefi) throw new InvalidOperationException("The captured GPT FFU requires UEFI boot.");
        foreach (var path in new[] { PowerShell, SystemTool("diskpart.exe"), SystemTool("bcdboot.exe"),
            image.Format == "Ffu" ? SystemTool("dism.exe") : settings.WimlibPath })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException("A required deployment tool is missing: " + Path.GetFileName(path));
        if (!uefi && !File.Exists(SystemTool("bootsect.exe"))) throw new InvalidOperationException("BIOS deployment requires bootsect.exe in WinPE.");
        if (image.Drivers.Count > 0 && (!File.Exists(SystemTool("dism.exe")) || DownloadDriver is null))
            throw new InvalidOperationException("Driver installation requires DISM and a driver download connection.");
        var ramRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        workspace = Path.Combine(ramRoot, "CloneDeploy", "Deployment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await using (var script = typeof(DeploymentMachine).Assembly.GetManifestResourceStream("CloneDeploy.Configure-Disk.ps1")!)
        await using (var output = File.Create(Path.Combine(workspace, "Configure-Disk.ps1"))) await script.CopyToAsync(output, ct);
        if (image.Format == "Ffu")
        {
            progress(new(DeploymentStage.Preparing, null) { Status = image.Performance.VerifyImage
                ? "Opening FFU source and verifying SHA-256 checksum" : "Opening FFU source and checking image size" });
            log.Write("FFU deployment transport: " + image.FfuTransport);
            if (image.FfuTransport == "Https")
            {
                var read = ReadFfuRange ?? throw new InvalidOperationException("HTTPS FFU reader is unavailable.");
                var tuning = image.Performance;
                var reader = new HttpsFfuReader(image.SizeBytes, (offset, count, token) => read(image, offset, count, token),
                    new(tuning.BlockMiB, tuning.CacheMiB, tuning.ParallelReads, tuning.VerifyImage), log);
                httpsReader = reader;
                ffuSource = await FfuDeploymentSource.OpenHttpsAsync(image, reader, log, ct);
            }
            else if (image.FfuTransport == "Smb")
                ffuSource = await FfuDeploymentSource.OpenAsync(image, direct =>
            {
                var connection = SmbCaptureConnection.Connect(direct, direct.Username!, direct.Password!);
                return (connection.FilePath, connection);
            }, ct);
            else throw new InvalidDataException("Unsupported FFU transport.");
            progress(new(DeploymentStage.Preparing, null) { Status = "Inspecting FFU image" });
            log.Write("Inspecting deployment FFU: " + ffuSource.FilePath);
            await tools.RunDismAsync(["/English", "/Get-ImageInfo", "/ImageFile:" + ffuSource.FilePath, "/Index:1"], ct);
            httpsReader?.ReportStats();
        }
        if (image.Format == "Wim" && image.WimTransport == "Smb")
        {
            progress(new(DeploymentStage.Preparing, null) { Status = image.Performance.VerifyImage
                ? "Opening SMB WIM and verifying SHA-256 checksum" : "Opening SMB WIM and checking image size" });
            WimSmbConnection.ValidateDeployment(image);
            var source = WimSmbConnection.Connect(image.Direct!);
            wimConnection = source.Connection;
            wimPath = source.Path;
            // Hold a read-only handle through apply so the checked image cannot be rewritten or deleted.
            wimFile = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (wimFile.Length != image.SizeBytes)
                throw new InvalidDataException("SMB WIM size differs from the assigned version.");
            if (image.Performance.VerifyImage)
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(wimFile, ct));
                if (!hash.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SMB WIM checksum verification failed. The target disk has not been changed.");
                log.Write("SMB WIM checksum verified before disk preparation.");
            }
            log.Write("WIM deployment transport: Smb (direct wimlib file access).");
        }
        var used = Environment.GetLogicalDrives().Select(x => char.ToUpperInvariant(x[0])).ToHashSet();
        var needed = image.Format == "Wim" ? 2 : 3;
        var available = Enumerable.Range('D', 'Z' - 'D' + 1).Select(x => (char)x).Where(x => x != 'X' && !used.Contains(x)).Reverse().Take(needed).ToArray();
        if (available.Length != needed) throw new InvalidOperationException($"{needed} free temporary drive letters are required for deployment.");
        // Match the deployed OS letter: wimlib restores absolute junctions relative
        // to its apply path. Configure-Disk checks C: ownership before cleaning.
        windowsLetter = image.Format == "Wim" ? 'C' : available[0];
        systemLetter = available[^2]; recoveryLetter = available[^1];

    }

    public async Task ApplyAsync(DeploymentExecution image, Func<Stream, CancellationToken, Task> download, Action<DeploymentProgress> progress, CancellationToken ct)
    {
        progress(new(DeploymentStage.Preparing, null) { Status = "Checking target disk and deployment tools" });
        DeploymentDisks.Revalidate(disk, ct);
        if (workspace is null) throw new InvalidOperationException("Deployment preflight was not completed.");
        var applyingStatus = image.Format == "Wim"
            ? "Downloading and applying Windows files" : "Applying FFU disk image";
        if (image.Format == "Wim")
        {
            if (image.WimTransport == "Smb" && (wimFile is null || wimPath is null))
                throw new InvalidOperationException("The SMB WIM source was not prepared.");
            progress(new(DeploymentStage.Preparing, null) { Status = "Partitioning and formatting target disk" });
            await Configure("PrepareWim", ct);
            progress(new(DeploymentStage.Deploying, null) { Status = applyingStatus });
            var wimProgress = new WimApplyProgress(progress);
            var arguments = WimApplyArguments(windowsLetter, image.Performance.VerifyImage);
            if (wimPath is not null) arguments[1] = wimPath;
            await tools.RunAsync(settings.WimlibPath, arguments, ct, wimPath is null ? download : null,
                outputLine: wimProgress.AcceptLine);
        }
        else
        {
            if (ffuSource is null) throw new InvalidOperationException("The FFU source was not connected and verified.");
            progress(new(DeploymentStage.Deploying, null) { Status = applyingStatus });
            try
            {
                await tools.RunDismAsync(["/English", "/Apply-FFU", "/ImageFile:" + ffuSource.FilePath, $@"/ApplyDrive:\\.\PhysicalDrive{disk.Number}"], ct,
                    progress: percent => progress(new(DeploymentStage.Deploying, percent) { Status = applyingStatus }));
            }
            finally { httpsReader?.ReportStats(); }
        }
        progress(new(DeploymentStage.Deploying, 100, true) { Status = "Image application complete" });
        progress(new(DeploymentStage.Finalizing, null) { Status = image.Format == "Wim"
            ? "Configuring Windows boot and recovery" : "Expanding FFU Windows partition and configuring boot and recovery" });
        await Configure(image.Format == "Wim" ? "FinishWim" : "FinishFfu", ct);
        var windowsRoot = await File.ReadAllTextAsync(Path.Combine(workspace, "windows-root.txt"), ct);
        if (image.DriverWarning is not null) log.Write($"WARNING: {image.DriverWarning}");
        if (image.Drivers.Count > 0)
        {
            var downloadDriver = DownloadDriver ?? throw new InvalidOperationException("Driver download is unavailable.");
            var driverRoot = Path.Combine(windowsRoot.Trim(), "Windows", "Temp", "CloneDeployDrivers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(driverRoot);
            try
            {
                foreach (var driver in image.Drivers)
                {
                    progress(new(DeploymentStage.Finalizing, null) { Status = $"Installing drivers: {driver.Name}" });
                    var archive = Path.Combine(driverRoot, driver.Id.ToString("N") + ".zip");
                    var expanded = Path.Combine(driverRoot, driver.Id.ToString("N"));
                    await downloadDriver(driver, archive, ct);
                    System.IO.Compression.ZipFile.ExtractToDirectory(archive, expanded);
                    if (!Directory.EnumerateFiles(expanded, "*.inf", SearchOption.AllDirectories).Any())
                        throw new InvalidDataException($"No INF drivers found in {driver.Name}.");
                    await tools.RunDismAsync(["/English", $"/Image:{windowsRoot.Trim()}", "/Add-Driver",
                        $"/Driver:{expanded}", "/Recurse", $"/ScratchDir:{driverRoot}"], ct);
                }
            }
            finally
            {
                try { Directory.Delete(driverRoot, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { log.Write($"WARNING: Could not remove temporary driver files: {ex.Message}"); }
            }
        }
        if (image.SetComputerName)
        {
            progress(new(DeploymentStage.Finalizing, null) { Status = "Setting Windows computer name" });
            OfflineComputerName.Apply(windowsRoot.Trim(), image.ComputerName, log);
        }
        if (image.SetComputerName)
        {
            progress(new(DeploymentStage.Finalizing, null) { Status = "Updating answer-file computer name" });
            await SysprepAnswerFiles.ApplyAsync(windowsRoot.Trim(), image, log, ct);
        }
        await SysprepDomainJoin.ApplyAsync(windowsRoot.Trim(), image,
            (request, token) => (ResolveOu ?? throw new InvalidOperationException("OU resolution is unavailable."))(request, progress, token), log, ct);
        await SysprepCredentials.ApplyAsync(windowsRoot.Trim(), ResolveSysprepCredentials, log, ct);
        SysprepSourceCleanup.Apply(windowsRoot.Trim(), message => log.Write(message));
        progress(new(DeploymentStage.Finalizing, null) { Status = "Checking for the agent in deployed Windows" });
        AgentInstalled = OfflineAgentDetection.IsInstalled(windowsRoot.Trim(), log);
        log.Write(AgentInstalled ? "CloneDeploy agent service and executable detected in deployed Windows."
            : "CloneDeploy agent not detected in deployed Windows; post-deployment policies will be skipped.");
    }
    private async Task Configure(string operation, CancellationToken ct)
    {
        var request = Path.Combine(workspace!, "request.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { Operation = operation, disk.Number, disk.UniqueId, disk.Size,
            Uefi = uefi, WindowsLetter = windowsLetter.ToString(), SystemLetter = systemLetter.ToString(), RecoveryLetter = recoveryLetter.ToString() }), ct);
        await tools.RunAsync(PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(workspace!, "Configure-Disk.ps1"), "-RequestFile", request], ct, null);
    }
    internal static string[] WimApplyArguments(char letter, bool verify) => verify
        ? ["apply", "-", "1", letter + @":\", "--check"] : ["apply", "-", "1", letter + @":\"];
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (wimFile is not null) await wimFile.DisposeAsync();
        }
        finally
        {
            wimConnection?.Dispose();
            if (ffuSource is not null) await ffuSource.DisposeAsync();
        }
    }
}
