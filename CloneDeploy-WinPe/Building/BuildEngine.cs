using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text;

namespace CloneDeploy_Server.WinPe;

/// <summary>
/// Builds Windows boot images and handles interrupted build requests.
/// </summary>
public sealed class BuildEngine(BuildStorage storage, WinPeOptions options)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(5);
    private const int ScratchSpaceMegabytes = 512;

    private string Dism => Path.Combine(options.AdkPath, "Deployment Tools", "amd64", "DISM", "dism.exe");
    public BuilderHealthDto Health()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return new(DateTime.UtcNow, false, "The builder requires 64-bit Windows.", null);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(DateTime.UtcNow, false, "Run the Windows builder with administrator privileges.", null);
        var required = new[] { Dism, Path.Combine(options.AdkPath, "Deployment Tools", "DandISetEnv.bat"),
            Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "copype.cmd"),
            Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "MakeWinPEMedia.cmd"),
            Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "amd64", "en-us", "winpe.wim"),
            Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "arm64", "en-us", "winpe.wim") };
        if (required.Any(x => !File.Exists(x)))
            return new(DateTime.UtcNow, false, "Install the ADK Deployment Tools and WinPE add-on, then check the builder's ADK path.", null);
        var installed = new BuildSetupEngine(storage, this).Health();
        if (!WinPeToolchainPolicy.InstalledVersionsMatch(installed))
            return new(DateTime.UtcNow, false, $"This application requires ADK and WinPE {ToolchainSpecDto.MicrosoftDefault.Version}. Use Builder Setup to install the required prerequisites.", installed.AdkVersion);
        return new(DateTime.UtcNow, true, "Ready to build x64 and ARM64 WinPE.", installed.AdkVersion);
    }

    public async Task BuildAsync(BuildRequestDto request, CancellationToken ct)
    {
        WinPeToolchainPolicy.ValidateBuildVersion(request.RequiredToolchainVersion);
        if (!WinPeKeyboardLayouts.IsSupported(request.KeyboardLayout)) throw new InvalidDataException("Unsupported WinPE keyboard layout.");
        var health = Health();
        if (!health.Ready) throw new InvalidOperationException(health.Message);
        var id = request.Id;
        await File.AppendAllTextAsync(storage.FilePath(id, "build.log"), $"[{DateTime.UtcNow:O}] Starting WinPE build {id}.\n", ct);
        if (!request.IsBase && request.UseBootEx && !File.ReadAllText(Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "MakeWinPEMedia.cmd")).Contains("/bootex", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This ADK does not support Windows UEFI 2023 CA media. Install a compatible ADK or turn off that option for this boot environment.");
        var job = storage.Job(id);
        BuildStorage.RejectLinks(job);
        if (!request.IsBase) await ImagerBundle.ValidateAsync(storage, id, request.Imager, ct);
        var work = Path.Combine(job, "work");
        var media = Path.Combine(work, "media");
        var mount = Path.Combine(job, "mount");
        if (Directory.Exists(work) || Directory.Exists(mount)) throw new InvalidOperationException("Build workspace already exists. Create a new build.");
        Directory.CreateDirectory(mount);
        await storage.WriteAsync(storage.FilePath(id, "toolchain.json"), Health(), ct);
        var mountAttempted = false;
        try
        {
            await storage.SetStatus(id, "Running", request.PrepareMediaAutomatically ? "Preparing WinPE media (x64)." : "Preparing recorded WinPE inputs.");
            if (request.PrepareMediaAutomatically)
                await PrepareMediaAsync(request, "amd64", work, mount, ct);
            else if (request.IsBase)
                await AdkCommand(id, "copype.cmd", "amd64 " + CmdQuote(work), ct);
            else
            {
                var archive = storage.FilePath(id, "base.zip");
                if (request.BaseBuildId is null || await BuildStorage.HashAsync(archive, ct) != request.BaseSha256)
                    throw new InvalidDataException("Base build checksum does not match the recorded input.");
                await BuildStorage.ExtractAsync(archive, work, ct);
            }
            var wim = Path.Combine(media, "sources", "boot.wim");
            if (!File.Exists(wim)) throw new InvalidDataException("The base media does not contain sources/boot.wim.");
            File.SetAttributes(wim, FileAttributes.Normal);
            mountAttempted = true;
            await Run(id, Dism, ["/Mount-Image", "/ImageFile:" + wim, "/Index:1", "/MountDir:" + mount], ct);
            if (!request.PrepareMediaAutomatically)
            {
                await storage.SetStatus(id, "Running", request.IsBase ? "Adding PowerShell components." : "Ensuring Imager discovery components.");
                var packages = Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "amd64", "WinPE_OCs");
                foreach (var component in PreparedWinPeMedia.Components)
                {
                    foreach (var cab in new[] { Path.Combine(packages, component + ".cab"), Path.Combine(packages, "en-us", component + "_en-us.cab") })
                        await Run(id, Dism, ["/Image:" + mount, "/Add-Package", "/PackagePath:" + cab], ct);
                }
            }
            foreach (var driver in request.Drivers)
            {
                await storage.SetStatus(id, "Running", "Adding drivers: " + driver.PackName + " / " + driver.Name);
                var archive = storage.FilePath(id, "driver-" + driver.FileId + ".zip");
                if (await BuildStorage.HashAsync(archive, ct) != driver.Sha256) throw new InvalidDataException("Driver archive checksum does not match: " + driver.Name);
                var directory = Path.Combine(job, "drivers", driver.FileId.ToString());
                await BuildStorage.ExtractAsync(archive, directory, ct);
                if (!Directory.EnumerateFiles(directory, "*.inf", SearchOption.AllDirectories).Any()) throw new InvalidDataException("Driver archive contains no INF files.");
                await Run(id, Dism, ["/Image:" + mount, "/Add-Driver", "/Driver:" + directory, "/Recurse"], ct);
            }
            if (!request.IsBase)
            {
                await Run(id, Dism, ["/Image:" + mount, $"/Set-ScratchSpace:{ScratchSpaceMegabytes}"], ct);
                await storage.SetStatus(id, "Running", "Installing CloneDeploy Imager bootstrap.");
                await ImagerBundle.InstallAsync(storage, id, request.Imager!, mount, ct);
                await Run(id, Dism, ["/Image:" + mount, "/Set-InputLocale:" + request.KeyboardLayout], ct);
            }
            if (!request.IsBase)
            {
                // Export PXE files without replacing protected tools inside the mounted OS.
                await WinPeBootFiles.CopyLegacyAsync(mount, job, ct);
            }
            await storage.SetStatus(id, "Running", "Saving the WIM.");
            await Run(id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Commit"], ct);
            mountAttempted = false;
            var artifacts = new List<BuildArtifactDto>();
            if (request.IsBase)
            {
                // Keep both media and firmware files. Variants must inherit a complete base working set.
                await Zip(id, work, "base-media.zip", ct);
                artifacts.Add(await Artifact(id, "base-media.zip", ct));
            }
            else
            {
                await storage.SetStatus(id, "Running", "Creating the bootable ISO.");
                await AdkCommand(id, "MakeWinPEMedia.cmd", "/ISO " + CmdQuote(work) + " " + CmdQuote(storage.FilePath(id, "winpe-x64.iso")) + (request.UseBootEx ? " /bootex" : ""), ct);
                // MakeWinPEMedia selects the EFI signing generation before exporting boot files.
                await WinPeBootFiles.CopyMediaAsync(media, job, ct);
                await CreateNativeBcdAsync(id, false, ct);
                await CreateNativeBcdAsync(id, true, ct);
                foreach (var name in WinPeBootFiles.VariantOutputs.Concat(WinPeBootFiles.NativeOutputs)) artifacts.Add(await Artifact(id, name, ct));
            }
            if (request.BuildArm64) artifacts.AddRange(await BuildArm64Async(request, ct));
            await storage.WriteAsync(storage.FilePath(id, "manifest.json"), new { Request = request, Toolchain = Health(), Artifacts = artifacts }, ct);
            artifacts.Add(await Artifact(id, "manifest.json", ct));
            await storage.SetStatus(id, "Ready", "Build completed.", artifacts);
        }
        catch
        {
            try { await RecoverAsync(id); mountAttempted = false; }
            catch { await File.WriteAllTextAsync(storage.FilePath(id, "cleanup-required.txt"), "Check this build's mount directories before restarting the builder."); throw; }
            throw;
        }
        finally
        {
            if (mountAttempted)
            {
                // Never perform global DISM cleanup: other products may own mounted images.
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try { await Run(id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Discard"], cleanup.Token); }
                catch
                {
                    // Mount may have failed before registering an image. Recovery queries DISM before unmounting.
                    try { await RecoverAsync(id); }
                    catch { await File.WriteAllTextAsync(storage.FilePath(id, "cleanup-required.txt"), "Check and unmount only this build's mount directory before restarting the builder."); throw; }
                }
            }
        }
    }
    private async Task PrepareMediaAsync(BuildRequestDto request, string architecture, string work, string mount, CancellationToken ct)
    {
        await new PreparedWinPeMedia(storage).RestoreOrPrepareAsync(request.RequiredToolchainVersion!, architecture, work, async archive =>
        {
            await storage.SetStatus(request.Id, "Running", $"Preparing {architecture} WinPE with PowerShell.");
            await AdkCommand(request.Id, "copype.cmd", architecture + " " + CmdQuote(work), ct);
            var wim = Path.Combine(work, "media", "sources", "boot.wim");
            File.SetAttributes(wim, FileAttributes.Normal);
            await Run(request.Id, Dism, ["/Mount-Image", "/ImageFile:" + wim, "/Index:1", "/MountDir:" + mount], ct);
            var packages = Path.Combine(options.AdkPath, "Windows Preinstallation Environment", architecture, "WinPE_OCs");
            foreach (var component in PreparedWinPeMedia.Components)
                foreach (var cab in new[] { Path.Combine(packages, component + ".cab"), Path.Combine(packages, "en-us", component + "_en-us.cab") })
                    await Run(request.Id, Dism, ["/Image:" + mount, "/Add-Package", "/PackagePath:" + cab], ct);
            await Run(request.Id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Commit"], ct);
            await using var output = new FileStream(archive, FileMode.CreateNew);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                BuildStorage.RejectLinks(file);
                var entry = zip.CreateEntry(Path.GetRelativePath(work, file).Replace('\\', '/'), CompressionLevel.Fastest);
                await using var input = File.OpenRead(file);
                await using var target = entry.Open();
                await input.CopyToAsync(target, ct);
            }
        }, ct);
    }

    private async Task<List<BuildArtifactDto>> BuildArm64Async(BuildRequestDto request, CancellationToken ct)
    {
        var id = request.Id;
        var job = storage.Job(id);
        var work = Path.Combine(job, "work-arm64");
        var mount = Path.Combine(job, "mount-arm64");
        if (Directory.Exists(work) || Directory.Exists(mount)) throw new InvalidOperationException("ARM64 workspace already exists. Create a new build.");
        Directory.CreateDirectory(mount);
        var mountAttempted = false;
        try
        {
            await storage.SetStatus(id, "Running", "Creating ARM64 WinPE media.");
            if (request.PrepareMediaAutomatically) await PrepareMediaAsync(request, "arm64", work, mount, ct);
            else if (request.IsBase) await AdkCommand(id, "copype.cmd", "arm64 " + CmdQuote(work), ct);
            else
            {
                var archive = storage.FilePath(id, "base-arm64.zip");
                if (request.BaseBuildId is null || request.BaseArm64Sha256 is null || await BuildStorage.HashAsync(archive, ct) != request.BaseArm64Sha256)
                    throw new InvalidDataException("ARM64 base checksum does not match the recorded input. Rebuild the base image.");
                await BuildStorage.ExtractAsync(archive, work, ct);
            }
            var media = Path.Combine(work, "media");
            var wim = Path.Combine(media, "sources", "boot.wim");
            if (!File.Exists(wim)) throw new InvalidDataException("ARM64 base media is missing sources/boot.wim.");
            File.SetAttributes(wim, FileAttributes.Normal);
            mountAttempted = true;
            await Run(id, Dism, ["/Mount-Image", "/ImageFile:" + wim, "/Index:1", "/MountDir:" + mount], ct);
            if (!request.PrepareMediaAutomatically)
            {
                var packages = Path.Combine(options.AdkPath, "Windows Preinstallation Environment", "arm64", "WinPE_OCs");
                foreach (var component in PreparedWinPeMedia.Components)
                    foreach (var cab in new[] { Path.Combine(packages, component + ".cab"), Path.Combine(packages, "en-us", component + "_en-us.cab") })
                        await Run(id, Dism, ["/Image:" + mount, "/Add-Package", "/PackagePath:" + cab], ct);
            }
            // ARM64 uses its own native tools and bootstrap; selected drivers apply to x64.
            if (!request.IsBase)
            {
                await ImagerBundle.InstallArm64Async(request.Imager ?? throw new InvalidDataException("Missing ARM64 Imager bootstrap settings."), mount, ct);
                await Run(id, Dism, ["/Image:" + mount, "/Set-InputLocale:" + request.KeyboardLayout], ct);
            }
            await Run(id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Commit"], ct);
            mountAttempted = false;
            var artifacts = new List<BuildArtifactDto>();
            if (request.IsBase)
            {
                await Zip(id, work, "base-media-arm64.zip", ct);
                artifacts.Add(await Artifact(id, "base-media-arm64.zip", ct));
            }
            else
            {
                await AdkCommand(id, "MakeWinPEMedia.cmd", "/ISO " + CmdQuote(work) + " " + CmdQuote(storage.FilePath(id, "winpe-arm64.iso")) + (request.UseBootEx ? " /bootex" : ""), ct);
                await WinPeBootFiles.CopyArm64MediaAsync(media, job, ct);
                await CreateArm64BcdAsync(id, ct);
                foreach (var name in WinPeBootFiles.Arm64Outputs.Concat(WinPeBootFiles.Arm64NativeOutputs)) artifacts.Add(await Artifact(id, name, ct));
            }
            return artifacts;
        }
        catch
        {
            try { await RecoverAsync(id); mountAttempted = false; }
            catch { await File.WriteAllTextAsync(storage.FilePath(id, "cleanup-required.txt"), "Check this build's ARM64 mount directories before restarting the builder."); throw; }
            throw;
        }
        finally
        {
            // copype can leave its own read-only mount behind if interrupted.
            if (mountAttempted)
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try { await Run(id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Discard"], cleanup.Token); }
                catch
                {
                    try { await RecoverAsync(id); }
                    catch { await File.WriteAllTextAsync(storage.FilePath(id, "cleanup-required.txt"), "Check this build's ARM64 mounts before restarting the builder."); throw; }
                }
            }
        }
    }

    private async Task CreateNativeBcdAsync(Guid id, bool efi, CancellationToken ct)
    {
        var path = storage.FilePath(id, efi ? "native-uefi.bcd" : "native-bios.bcd");
        var executable = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        var loader = "{" + Guid.NewGuid().ToString("D") + "}";
        var prefix = $@"\WinPe\{id:N}";
        Task Edit(params string[] args) => Run(id, executable, ["/store", path, .. args], ct);
        await Run(id, executable, ["/createstore", path], ct);
        await Edit("/create", "{ramdiskoptions}", "/d", "WinPE RAM disk");
        await Edit("/set", "{ramdiskoptions}", "ramdisksdidevice", "boot");
        await Edit("/set", "{ramdiskoptions}", "ramdisksdipath", prefix + @"\boot.sdi");
        await Edit("/create", loader, "/d", "CloneDeploy WinPE", "/application", "osloader");
        foreach (var property in new[] { "device", "osdevice" })
            await Edit("/set", loader, property, "ramdisk=[boot]" + prefix + @"\boot.wim,{ramdiskoptions}");
        await Edit("/set", loader, "path", efi ? @"\Windows\System32\winload.efi" : @"\Windows\System32\winload.exe");
        await Edit("/set", loader, "systemroot", @"\Windows");
        await Edit("/set", loader, "winpe", "Yes");
        await Edit("/set", loader, "detecthal", "Yes");
        await Edit("/create", "{bootmgr}", "/d", "Windows Boot Manager");
        await Edit("/displayorder", loader, "/addlast");
        await Edit("/default", loader);
        await Edit("/timeout", "0");
    }

    private async Task CreateArm64BcdAsync(Guid id, CancellationToken ct)
    {
        var path = storage.FilePath(id, "native-arm64.bcd");
        var executable = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        var loader = "{" + Guid.NewGuid().ToString("D") + "}";
        var prefix = $@"\WinPe\{id:N}\arm64";
        Task Edit(params string[] args) => Run(id, executable, ["/store", path, .. args], ct);
        await Run(id, executable, ["/createstore", path], ct);
        await Edit("/create", "{ramdiskoptions}", "/d", "WinPE ARM64 RAM disk");
        await Edit("/set", "{ramdiskoptions}", "ramdisksdidevice", "boot");
        await Edit("/set", "{ramdiskoptions}", "ramdisksdipath", prefix + @"\boot.sdi");
        await Edit("/create", loader, "/d", "CloneDeploy ARM64 WinPE", "/application", "osloader");
        foreach (var property in new[] { "device", "osdevice" })
            await Edit("/set", loader, property, "ramdisk=[boot]" + prefix + @"\boot.wim,{ramdiskoptions}");
        await Edit("/set", loader, "path", @"\Windows\System32\winload.efi");
        await Edit("/set", loader, "systemroot", @"\Windows");
        await Edit("/set", loader, "winpe", "Yes");
        await Edit("/set", loader, "detecthal", "Yes");
        await Edit("/create", "{bootmgr}", "/d", "Windows Boot Manager");
        await Edit("/displayorder", loader, "/addlast");
        await Edit("/default", loader);
        await Edit("/timeout", "0");
    }

    public async Task RecoverAsync(Guid id)
    {
        // Include copype's temporary mounts as well as our customization mounts.
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        var output = await Run(id, Dism, ["/English", "/Get-MountedWimInfo"], timeout.Token);
        foreach (var relative in new[] { "mount", "mount-arm64", "work/mount", "work-arm64/mount" })
        {
            var mount = Path.GetFullPath(Path.Combine(storage.Job(id), relative));
            BuildStorage.RejectLinks(mount);
            if (Directory.Exists(mount) && output.Split('\n').Any(line => line.Trim().EndsWith(": " + mount, StringComparison.OrdinalIgnoreCase)))
                await Run(id, Dism, ["/Unmount-Image", "/MountDir:" + mount, "/Discard"], timeout.Token);
        }
        await storage.SetStatus(id, "Failed", "The builder stopped during this build. Create a new build to retry.");
    }
    private async Task Zip(Guid id, string source, string name, CancellationToken ct)
    {
        await using var output = new FileStream(storage.FilePath(id, name), FileMode.CreateNew);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); BuildStorage.RejectLinks(file);
            var entry = zip.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
            await using var input = File.OpenRead(file); await using var target = entry.Open(); await input.CopyToAsync(target, ct);
        }
    }
    private async Task<BuildArtifactDto> Artifact(Guid id, string name, CancellationToken ct)
    {
        var path = storage.FilePath(id, name);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("Missing build output: " + name);
        return new(name, new FileInfo(path).Length, await BuildStorage.HashAsync(path, ct));
    }
    public static string CmdQuote(string path)
    {
        if (path.IndexOfAny(['"', '%', '!', '\r', '\n', '&', '|', '<', '>', '^']) >= 0)
            throw new InvalidDataException("ADK and build paths cannot contain shell control characters.");
        return "\"" + path + "\"";
    }
    private async Task AdkCommand(Guid id, string command, string arguments, CancellationToken ct)
    {
        var wrapper = storage.FilePath(id, "adk-command.cmd");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\ncall " + CmdQuote(Path.Combine(options.AdkPath, "Deployment Tools", "DandISetEnv.bat")) +
            "\r\nif errorlevel 1 exit /b %errorlevel%\r\ncall " + CmdQuote(Path.Combine(options.AdkPath, "Windows Preinstallation Environment", command)) +
            " " + arguments + "\r\nexit /b %errorlevel%\r\n", Encoding.ASCII, ct);
        await Run(id, Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], ct, "/d /s /c \"" + CmdQuote(wrapper) + "\"");
    }
    private async Task<string> Run(Guid id, string executable, string[] arguments, CancellationToken ct, string? rawArguments = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        ConfigureProcess(start, storage.Job(id));
        if (rawArguments is not null) start.Arguments = rawArguments;
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        await File.AppendAllTextAsync(storage.FilePath(id, "build.log"), $"\n[{DateTime.UtcNow:O}] {Path.GetFileName(executable)} {string.Join(' ', arguments)}\n", ct);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await File.AppendAllTextAsync(storage.FilePath(id, "build.log"), await stdout + await stderr); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}. See the build log.");
        return await stdout;
    }
    internal static void ConfigureProcess(ProcessStartInfo start, string directory)
    {
        start.WorkingDirectory = directory;
        var temporary = Path.Combine(directory, "temp");
        Directory.CreateDirectory(temporary);
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["COMSPEC"] = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        start.Environment["PATH"] = string.Join(';', Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0"));
    }
}
