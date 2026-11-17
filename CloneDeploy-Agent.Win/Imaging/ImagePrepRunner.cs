using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal sealed class ImagePrepRunner(AgentOptions options, IPolicyProcess process)
{
    internal string WindowsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    internal string SystemDirectory { get; init; } = Environment.SystemDirectory;

    internal static void Validate(AgentImagePrepPlan plan)
    {
        if (plan.ResetAgent && plan.UninstallAgent)
            throw new InvalidOperationException("Image prep cannot both reset and uninstall the agent.");
        if (plan.RebootAndCapture != (plan.Capture is not null))
            throw new InvalidOperationException("The image prep capture destination is missing or inconsistent.");
        var error = ImagePrepAnswerFile.Validate(plan.AnswerFile);
        if (error is not null)
            throw new InvalidOperationException(error);
        if (plan.SetupComplete?.Length > ImagePrepAnswerFile.MaximumContentLength
            || plan.PreSysprepScript?.Length > ImagePrepAnswerFile.MaximumContentLength)
            throw new InvalidOperationException("Image prep scripts exceed the size limit.");
    }

    public async Task RunAsync(AgentImagePrepPlan plan, Action<string, int> progress, CancellationToken ct)
    {
        Validate(plan);
        var work = Path.Combine(options.StateDirectory, "ImagePrep");
        Directory.CreateDirectory(work);

        if (plan.DisableHibernate)
        {
            progress("Disabling hibernation.", 5);
            await RunAsync(Path.Combine(SystemDirectory, "powercfg.exe"), ["/hibernate", "off"], 5, ct);
        }

        if (plan.DisableBitLocker)
        {
            progress("Turning off BitLocker on fixed volumes and waiting for decryption.", 10);
            await PowerShellAsync(work, "bitlocker.ps1", """
                $ErrorActionPreference = 'Stop'
                $volumes = @(Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -ErrorAction Stop)
                foreach ($volume in $volumes) {
                    $logical = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($volume.DriveLetter)'"
                    if ($logical.DriveType -ne 3) { continue }
                    $state = Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus
                    if ($state.ReturnValue -ne 0) { throw 'Could not read BitLocker conversion status.' }
                    if ($state.ConversionStatus -ne 0) {
                        $result = Invoke-CimMethod -InputObject $volume -MethodName Decrypt
                        if ($result.ReturnValue -ne 0) { throw "Could not decrypt $($volume.DriveLetter): $($result.ReturnValue)" }
                    }
                    do {
                        $state = Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus
                        if ($state.ReturnValue -ne 0) { throw 'Could not read BitLocker conversion status.' }
                        if ($state.ConversionStatus -ne 0) { Start-Sleep -Seconds 10 }
                    } while ($state.ConversionStatus -ne 0)
                }
                """, 1440, ct);
        }

        if (!string.IsNullOrWhiteSpace(plan.PreSysprepScript))
        {
            progress("Running the pre-Sysprep PowerShell script.", 40);
            var exitCode = await PowerShellAsync(work, "pre-sysprep.ps1", plan.PreSysprepScript, 60, ct, requireSuccess: false);
            progress($"Pre-Sysprep script finished with exit code {exitCode}. Continuing image preparation.", 45);
        }

        var ticketPath = Path.Combine(WindowsDirectory, "Setup", "Scripts", "CloneDeploy-SysprepCredentials.ticket");
        if (!string.IsNullOrWhiteSpace(plan.SysprepCredentialTicket))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ticketPath)!);
            await WriteAsync(ticketPath, plan.SysprepCredentialTicket, ct);
        }
        else if (File.Exists(ticketPath)) File.Delete(ticketPath);

        string? answerPath = null;
        if (!string.IsNullOrWhiteSpace(plan.AnswerFile))
        {
            progress("Writing the Sysprep answer file.", 55);
            answerPath = Path.Combine(SystemDirectory, "Sysprep", "unattend.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(answerPath)!);
            var document = XDocument.Parse(plan.AnswerFile, LoadOptions.PreserveWhitespace);
            document.Declaration = new XDeclaration("1.0", "utf-8", null);
            await WriteAsync(answerPath, $"{document.Declaration}\r\n{document}", ct);
        }

        if (!string.IsNullOrWhiteSpace(plan.SetupComplete))
        {
            progress("Adding SetupComplete commands.", 65);
            var scripts = Path.Combine(WindowsDirectory, "Setup", "Scripts");
            Directory.CreateDirectory(scripts);
            var path = Path.Combine(scripts, "SetupComplete.cmd");
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
            await WriteAsync(path, AddSetupComplete(existing, plan.SetupComplete), ct);
        }

        // The independent finalizer runs Sysprep only after cleanup and capture boot preparation.
        await ImagePrepPowerAction.SaveAsync(options.StateDirectory, plan.RunSysprep, answerPath is not null, ct);
        progress("Preparation complete. Finalizing agent state before the final power action.", 95);
    }

    internal static string AddSetupComplete(string existing, string commands)
    {
        const string begin = "REM BEGIN CLONEDEPLOY IMAGE PREP";
        const string end = "REM END CLONEDEPLOY IMAGE PREP";
        var first = existing.IndexOf(begin, StringComparison.Ordinal);
        if (first >= 0)
        {
            var last = existing.IndexOf(end, first, StringComparison.Ordinal);
            if (last < 0)
                throw new InvalidOperationException("SetupComplete contains an incomplete CloneDeploy block. Correct it before retrying.");
            existing = existing.Remove(first, last + end.Length - first).TrimStart('\r', '\n');
        }

        return $"{begin}\r\n{commands.TrimEnd()}\r\n{end}\r\n{existing}";
    }

    private static async Task WriteAsync(string path, string text, CancellationToken ct)
    {
        if (File.Exists(path) && !File.Exists($"{path}.clonedeploy-backup"))
            File.Copy(path, $"{path}.clonedeploy-backup");
        await File.WriteAllTextAsync($"{path}.tmp", text, new UTF8Encoding(false), ct);
        File.Move($"{path}.tmp", path, true);
    }

    private async Task<int> PowerShellAsync(string work, string name, string script, int timeout, CancellationToken ct, bool requireSuccess = true)
    {
        var path = Path.Combine(work, name);
        try
        {
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(true), ct);
            return await RunAsync(Path.Combine(SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path], timeout, ct, requireSuccess);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<int> RunAsync(string executable, IEnumerable<string> arguments, int timeout, CancellationToken ct, bool requireSuccess = true)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        var result = await process.RunAsync(start, timeout, ct);
        if (requireSuccess && result.ExitCode != 0)
            throw new InvalidOperationException($"Image prep stopped: {Path.GetFileName(executable)} returned exit code {result.ExitCode}. Check its logs before retrying.");
        return result.ExitCode;
    }
}
