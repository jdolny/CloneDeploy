using CloneDeploy_Agent.Contracts;
using System.Text.Json;
using System.Diagnostics;

namespace CloneDeploy_Agent;

/// <summary>Builds explicit WinGet operations and recognizes successful update no-ops.</summary>
public static class WinGetOperation
{
    private const uint MissingDll = 0xC0000135;
    private const uint NameNotResolved = 0x80072EE7;
    private const uint CannotConnect = 0x80072EFD;
    private const uint RequestTimedOut = 0x80072EE2;
    private const uint SecureConnectionFailed = 0x80072F8F;
    private const uint DownloadFailed = 0x8A150008;
    private const uint SourceDataMissing = 0x8A15000F;
    private const uint SourceOpenFailed = 0x8A150045;
    private const uint FailedToOpenAllSources = 0x8A15004B;

    private static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(30);

    // Microsoft winget-cli AppInstallerErrors.h.
    private const int NoApplicationsFound = unchecked((int)0x8A150014);
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);

    public static int Operation(JsonElement settings) =>
        !settings.Flag("ExplicitWinGetOperation")
        && settings.Number("WinGetInstallType") == (int)WinGetInstallType.Install
        && settings.Flag("KeepUpdated")
            ? (int)WinGetInstallType.Update
            : settings.Number("WinGetInstallType");

    public static string Arguments(JsonElement settings)
    {
        var selectedScope = (WinGetScope)settings.Number("WinGetScope");
        WinGetScopeOptions.Resolve(selectedScope, settings.Text("Arguments"), settings.Text("AdditionalArguments"));
        var operation = Operation(settings);
        var verb = operation switch
        {
            (int)WinGetInstallType.Install => "install",
            (int)WinGetInstallType.Update or (int)WinGetInstallType.UpdateAll => "upgrade",
            _ => throw new InvalidDataException("Invalid direct WinGet operation.")
        };
        var arguments = $"{verb}{(operation == (int)WinGetInstallType.UpdateAll
                                 ? " --all"
                                 : $" --id {PolicyActionRunner.Quote(settings.Text("PackageId"))} --exact")} --silent --disable-interactivity --accept-source-agreements";
        arguments += " --accept-package-agreements";
        if (selectedScope != WinGetScope.Default)
            arguments += selectedScope == WinGetScope.Machine ? " --scope machine" : " --scope user";

        if (operation == (int)WinGetInstallType.Install && !settings.Flag("InstallLatest")
            && settings.Text("PackageVersion").Length > 0)
            arguments += $" --version {PolicyActionRunner.Quote(settings.Text("PackageVersion"))}";

        if (settings.Text("Override").Length > 0)
            arguments += $" --override {PolicyActionRunner.Quote(settings.Text("Override"))}";

        return $"{arguments} {settings.Text("Arguments")} {settings.Text("AdditionalArguments")}";
    }

    public static async Task<PolicyProcessResult> RunAsync(
        JsonElement settings,
        ProcessStartInfo start,
        Func<ProcessStartInfo, CancellationToken, Task<PolicyProcessResult>> execute,
        CancellationToken ct)
    {
        // Probe in the same execution context as the install, including scheduled Run As.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeTimeout.CancelAfter(StartupProbeTimeout);
        PolicyProcessResult probe;

        try
        {
            var probeStart = new ProcessStartInfo(start.FileName, "--version")
            {
                WorkingDirectory = start.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var variable in start.Environment) probeStart.Environment[variable.Key] = variable.Value;
            probe = await execute(probeStart, probeTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(
                1,
                "",
                "WinGet startup check timed out after 30 seconds. Check App Installer in the account used to run this action. Installation was not attempted.");
        }

        if (probe.ExitCode != 0)
        {
            var failure = DescribeFailure(probe);

            return failure with
            {
                Error = $"WinGet startup check failed; installation was not attempted.\n{failure.Error}"
            };
        }

        return Normalize(settings, await execute(start, ct));
    }

    public static PolicyProcessResult Normalize(JsonElement settings, PolicyProcessResult result) =>
        Operation(settings) is (int)WinGetInstallType.Update or (int)WinGetInstallType.UpdateAll
        && result.ExitCode is NoApplicationsFound or UpdateNotApplicable
            ? new(0, $"{result.Output}\nNo installed package needs an update.", result.Error)
            : DescribeFailure(result);

    private static PolicyProcessResult DescribeFailure(PolicyProcessResult result)
    {
        if (result.ExitCode is ProcessExitCodes.Success or ProcessExitCodes.RebootRequired)
            return result;

        var reason = unchecked((uint)result.ExitCode) switch
        {
            MissingDll => "A required DLL could not be loaded. Repair or update App Installer (WinGet) and its runtime dependencies for the account running this action. This code does not indicate an internet connectivity failure.",
            NameNotResolved => "The source or download hostname could not be resolved. Check DNS and network access for the account running this action.",
            CannotConnect => "Could not connect to the source or download server. Check internet access, proxy settings and firewall rules for the account running this action.",
            RequestTimedOut => "The source or download request timed out. Check connectivity, proxy settings and server availability.",
            SecureConnectionFailed => "The secure connection failed. Check certificate trust, the device clock and any HTTPS-inspecting proxy.",
            DownloadFailed => "WinGet could not download the package. Check the reported output, download URL access, proxy settings and firewall rules.",
            SourceDataMissing or SourceOpenFailed or FailedToOpenAllSources => "WinGet could not load its package source. Check source configuration and access from the account running this action.",
            unchecked((uint)NoApplicationsFound) => "No matching package was found. Check the package ID and configured WinGet sources.",
            _ => "Review the WinGet output below for the failure details."
        };

        return result with
        {
            Error = $"WinGet failed with exit code {result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8}). {reason}\n{result.Error}"
        };
    }
}