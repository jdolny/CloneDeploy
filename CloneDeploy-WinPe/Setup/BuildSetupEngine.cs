using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace CloneDeploy_Server.WinPe;

/// <summary>
/// Installs and checks the Windows tools required to build boot images.
/// </summary>
public sealed class BuildSetupEngine(BuildStorage storage, BuildEngine builds)
{
    private const int BootTimeToleranceSeconds = 10;
    private const int SuccessRebootRequired = 3010;
    private const int SuccessRebootInitiated = 1641;
    private const int MaximumDownloadUrlCharacters = 2048;
    private const int CopyBufferBytes = 81_920;
    private const int MaximumDownloadRedirects = 5;
    private const int FirstRedirectStatusCode = 300;
    private const int FirstClientErrorStatusCode = 400;
    private const int BytesPerMebibyte = 1024 * 1024;
    private const long MaximumBootstrapperBytes = 128L * BytesPerMebibyte;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private string RestartMarker => Path.Combine(storage.Root, "setup-restart.json");
    private string BlockMarker => Path.Combine(storage.Root, "setup-incomplete.json");
    public bool BlocksBuilds => File.Exists(BlockMarker) || Health().RestartRequired;
    private static DateTime BootTime => DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64);
    public SetupHealthDto Health()
    {
        string? adk = null, pe = null;
        var elevated = false;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var key in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(key);
                    var name = entry?.GetValue("DisplayName") as string;
                    if (name == "Windows Assessment and Deployment Kit") adk = entry?.GetValue("DisplayVersion") as string;
                    if (name == "Windows Assessment and Deployment Kit Windows Preinstallation Environment Add-ons") pe = entry?.GetValue("DisplayVersion") as string;
                }
            }
        }
        var restart = storage.Read<RestartRecord>(RestartMarker);
        return new(DateTime.UtcNow, WinPeToolchainPolicy.Protocol, adk, pe, elevated,
            restart is not null && Math.Abs((restart.BootTimeUtc - BootTime).TotalSeconds) < BootTimeToleranceSeconds)
        { RequiredVersion = ToolchainSpecDto.MicrosoftDefault.Version };
    }
    public Task Heartbeat(CancellationToken ct) => storage.WriteAsync(Path.Combine(storage.Root, "setup-health.json"), Health(), ct);
    public Task SetStatus(Guid id, string state, string message) => storage.WriteAsync(SetupFiles.PathFor(storage, id, "status.json"), new SetupStatusDto(state, message, DateTime.UtcNow));
    public static void ValidateTransition(ToolchainSpecDto spec)
    {
        WinPeToolchainPolicy.ValidateRequest(spec);
    }
    public async Task RunAsync(SetupRequestDto request, CancellationToken stop)
    {
        WinPeToolchainPolicy.ValidateRequest(request.Toolchain);
        var id = request.Id;
        BuildStorage.RejectLinks(SetupFiles.DirectoryPath(storage, id));
        await Log(id, "Starting prerequisite installation for " + request.Toolchain.Version);
        var inventory = Health();
        if (!inventory.Elevated || !OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new InvalidOperationException("Prerequisite installation requires the elevated x64 Windows builder.");
        if (inventory.RestartRequired) throw new InvalidOperationException("Restart Windows before installing or building again.");
        foreach (var name in new[] { "adksetup", "adkwinpesetup" })
            foreach (var running in Process.GetProcessesByName(name))
                using (running) if (!running.HasExited) throw new InvalidOperationException("An ADK or WinPE installer is still running. Wait for it to finish before retrying setup.");
        ValidateTransition(request.Toolchain);
        await SetStatus(id, "Checking", "Checking for mounted images.");
        var mounts = await ProcessAsync(id, Path.Combine(Environment.SystemDirectory, "dism.exe"), ["/English", "/Get-MountedImageInfo"], stop);
        if (mounts.ExitCode != 0 || mounts.Output.Contains("Mount Dir", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unmount existing images before changing ADK prerequisites.");
        var spec = request.Toolchain;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(DownloadTimeout);
        var adk = SetupFiles.PathFor(storage, id, "adksetup.exe");
        var pe = SetupFiles.PathFor(storage, id, "adkwinpesetup.exe");
        // Validate both bootstrappers before allowing either to change the machine.
        await Download(id, "ADK", spec.AdkUrl, spec.AdkSha256, adk, timeout.Token);
        await Download(id, "WinPE", spec.WinPeUrl, spec.WinPeSha256, pe, timeout.Token);
        await VerifyFileAsync(id, adk, spec.AdkSha256, spec.Version, timeout.Token);
        await VerifyFileAsync(id, pe, spec.WinPeSha256, spec.Version, timeout.Token);
        foreach (var item in new[] { (File: adk, Name: "ADK", Feature: "OptionId.DeploymentTools"),
                     (File: pe, Name: "WinPE", Feature: "OptionId.WindowsPreinstallationEnvironment") })
        {
            stop.ThrowIfCancellationRequested();
            await SetStatus(id, "Installing", "Installing " + item.Name + ". Installer logs are available below.");
            await storage.WriteAsync(BlockMarker, request.Id);
            // Once launched, do not kill an MSI/bootstrapper halfway through servicing. Shutdown waits for it.
            var result = await ProcessAsync(id, item.File,
                ["/quiet", "/features", item.Feature, "/ceip", "off", "/log", SetupFiles.PathFor(storage, id, item.Name.ToLowerInvariant() + "-install.log"), "/norestart"], CancellationToken.None);
            await Log(id, item.Name + " installer exit code: " + result.ExitCode);
            if (result.ExitCode is SuccessRebootRequired or SuccessRebootInitiated)
            {
                await storage.WriteAsync(RestartMarker, new RestartRecord(BootTime));
                await SetStatus(id, "RestartRequired", "Windows restart required. After restarting, install prerequisites again to finish or verify the application-required toolchain.");
                return;
            }
            if (result.ExitCode != 0) throw new InvalidOperationException(item.Name + " installer failed (" + result.ExitCode + "). See its log. Some existing versions require manual removal before installing the pinned version; the builder does not automatically uninstall kits.");
        }
        await SetStatus(id, "Verifying", "Checking installed versions and build tools.");
        var actual = Health();
        if (actual.AdkVersion != spec.Version || actual.WinPeVersion != spec.Version || !builds.Health().Ready)
            throw new InvalidOperationException("Installed versions or x64/ARM64 WinPE files do not match the application-required toolchain. Check installer logs and the builder's ADK path.");
        await SetStatus(id, "Succeeded", "ADK and WinPE " + spec.Version + " installed and verified.");
        File.Delete(BlockMarker);
    }
    public static Uri ValidateInstallerUrl(string url)
    {
        if (url?.Length > MaximumDownloadUrlCharacters || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Installer URLs must use HTTPS without embedded credentials or fragments.");
        return uri;
    }

    private async Task Download(Guid id, string name, string url, string expected, string destination, CancellationToken ct)
    {
        await SetStatus(id, "Downloading", "Downloading " + name + " installer.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = ValidateInstallerUrl(url);
        for (var redirects = 0; redirects <= MaximumDownloadRedirects; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= FirstRedirectStatusCode and < FirstClientErrorStatusCode && response.Headers.Location is { } location)
            { uri = ValidateInstallerUrl(new Uri(uri, location).AbsoluteUri); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBootstrapperBytes) throw new InvalidDataException("Installer exceeds the 128 MB bootstrapper limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, true))
            {
                var buffer = new byte[CopyBufferBytes]; long received = 0, reported = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    received += read;
                    if (received > MaximumBootstrapperBytes) throw new InvalidDataException("Installer exceeds the 128 MB bootstrapper limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (received - reported >= BytesPerMebibyte) { reported = received; await SetStatus(id, "Downloading", $"Downloading {name}: {received / (double)BytesPerMebibyte:N1} MB."); }
                }
            }
            if (!string.Equals(await BuildStorage.HashAsync(destination, ct), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(name + " SHA-256 checksum mismatch. Nothing has been installed.");
            await Log(id, name + " download checksum verified."); return;
        }
        throw new InvalidDataException("Installer download exceeded the redirect limit.");
    }
    private async Task VerifyInstaller(Guid id, string path, string version, CancellationToken ct)
    {
        await SetStatus(id, "Verifying", "Verifying Microsoft signatures and installer versions.");
        const string script = @"$ErrorActionPreference='Stop'; Import-Module ($PSHOME + '\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop; $s=Get-AuthenticodeSignature -LiteralPath $env:CLONEDEPLOY_SETUP_FILE; $s | Format-List Status,StatusMessage; if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or $s.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) -ne 'Microsoft Corporation'){exit 1}; exit 0";
        var result = await ProcessAsync(id, Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script], ct, path);
        if (result.ExitCode != 0) throw new InvalidDataException("Installer does not have a valid Microsoft Corporation signature.");
        if (FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim() != version) throw new InvalidDataException("Installer version does not match the configured toolchain.");
        await Log(id, Path.GetFileName(path) + " Microsoft signature and version verified.");
    }
    public async Task VerifyFileAsync(Guid id, string path, string sha256, string version, CancellationToken ct)
    {
        if (!string.Equals(await BuildStorage.HashAsync(path, ct), sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer SHA-256 checksum mismatch.");
        await VerifyInstaller(id, path, version, ct);
    }
    private Task Log(Guid id, string message) => File.AppendAllTextAsync(SetupFiles.PathFor(storage, id, "setup.log"), $"[{DateTime.UtcNow:O}] {message}\n");
    private async Task<(int ExitCode, string Output)> ProcessAsync(Guid id, string exe, string[] args, CancellationToken ct, string? signatureFile = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        BuildEngine.ConfigureProcess(start, SetupFiles.DirectoryPath(storage, id));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        if (signatureFile is not null) start.Environment["CLONEDEPLOY_SETUP_FILE"] = signatureFile;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start prerequisite tool.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        finally { await Log(id, await output + await error); }
        return (process.ExitCode, await output);
    }
    private sealed record RestartRecord(DateTime BootTimeUtc);
}
