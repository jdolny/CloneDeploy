using System.ComponentModel;
using System.Diagnostics;
using CloneDeploy_Imager.Diagnostics;
using Microsoft.Win32;

namespace CloneDeploy_Imager.Deployment;

internal static class OfflineComputerName
{
    private const int MaximumControlSetNumber = 999;


    internal static void Validate(string name)
    {
        if (CloneDeploy_Agent.Contracts.ComputerNameValidation.Error(name) is { } error)
            throw new InvalidDataException(error);
    }

    public static void Apply(string windowsRoot, string name, DiagnosticLog log)
    {
        Validate(name);
        var config = Path.Combine(windowsRoot, @"Windows\System32\config");
        WithLoadedHive(Path.Combine(config, "SYSTEM"), writable: true, log, hive =>
        {
            WriteName(hive, name);
            hive.Flush();
        });
        log.Write($"Offline Windows computer name set to {name}.");

        WithLoadedHive(Path.Combine(config, "SOFTWARE"), writable: false, log, software =>
        {
            using var state = software.OpenSubKey(@"Microsoft\Windows\CurrentVersion\Setup\State");
            log.Write($"Deployed Windows setup state: {state?.GetValue("ImageState") as string ?? "unknown"}.");
        }, optionalLoad: true);
    }

    internal static void WithLoadedHive(string file, bool writable, DiagnosticLog log,
        Action<RegistryKey> action, bool optionalLoad = false)
    {
        // Mount only our own unique key; never unload a pre-existing registry mount.
        var mount = "CloneDeploy_" + Guid.NewGuid().ToString("N");
        if (RunRegistryTool("load", mount, file, log) != 0)
        {
            if (!optionalLoad)
                throw new IOException("Unable to load the deployed Windows registry. See registry tool diagnostics.");
            log.Write("Windows setup state could not be read; offline registry naming was applied independently of answer-file presence.");
            return;
        }

        var completed = false;
        try
        {
            using var hive = Registry.LocalMachine.OpenSubKey(mount, writable)
                ?? throw new IOException("Unable to open the loaded deployed Windows registry.");
            action(hive);
            completed = true;
        }
        finally
        {
            // All managed registry handles are disposed before unloading, including on edit failure.
            try
            {
                if (RunRegistryTool("unload", mount, null, log) != 0 && completed)
                    throw new IOException("Unable to unload the deployed Windows registry. See registry tool diagnostics.");
            }
            catch (Exception ex) when (!completed)
            {
                // Preserve the original edit failure; the unload attempt is logged separately.
                log.Failure("Unloading deployed Windows registry after edit failure", ex);
            }
        }
    }

    private static int RunRegistryTool(string operation, string mount, string? file, DiagnosticLog log)
    {
        using var process = new Process
        {
            StartInfo = new(Path.Combine(Environment.SystemDirectory, "reg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(operation);
        process.StartInfo.ArgumentList.Add(@"HKLM\" + mount);
        if (file is not null) process.StartInfo.ArgumentList.Add(file);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            log.Write($"Offline registry {operation}: exit {process.ExitCode}.\n{output.Result}\n{error.Result}");
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            log.Write($"Offline registry {operation} could not start: Windows error {ex.NativeErrorCode} ({ex.Message}).");
            throw;
        }
    }

    internal static void WriteName(RegistryKey hive, string name)
    {
        Validate(name);
        using var select = hive.OpenSubKey("Select");
        if (select?.GetValue("Default") is not int controlSet || controlSet is < 1 or > MaximumControlSetNumber)
            throw new InvalidDataException("The deployed Windows default control set is invalid.");

        using var computer = hive.OpenSubKey($@"ControlSet{controlSet:000}\Control\ComputerName\ComputerName", writable: true)
            ?? throw new InvalidDataException("The deployed Windows computer-name registry key is missing.");
        using var tcpip = hive.OpenSubKey($@"ControlSet{controlSet:000}\Services\Tcpip\Parameters", writable: true)
            ?? throw new InvalidDataException("The deployed Windows TCP/IP registry key is missing.");
        computer.SetValue("ComputerName", name, RegistryValueKind.String);
        tcpip.SetValue("Hostname", name, RegistryValueKind.String);
        tcpip.SetValue("NV Hostname", name, RegistryValueKind.String);
        if (!Equals(computer.GetValue("ComputerName"), name) || !Equals(tcpip.GetValue("Hostname"), name)
            || !Equals(tcpip.GetValue("NV Hostname"), name))
            throw new IOException("The deployed Windows computer-name registry values could not be verified.");
    }

}
