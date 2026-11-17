using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Prepares and runs individual actions, including commands, scripts, and software installations.
/// </summary>
public sealed class PolicyActionRunner(IPolicyProcess process)
{
    private const string UnquotedCommandPrefix = "[unquote]";

    public DomainJoinRunner DomainJoin { get; set; } = new(new WindowsDomainJoinMachine());

    public IRunAsProcess? RunAs { get; set; }

    public PortalNotificationJournal? Notifications { get; set; }

    public bool UserContext { get; init; }

    public static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string PsQuote(string value) => $"'{value.Replace("'", "''")}'";

    public static string WindowsTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    public static string PowerShell => WindowsTool(@"WindowsPowerShell\v1.0\powershell.exe");

    public static void Validate(AgentPolicyContent policy)
    {
        var s = policy.Settings;
        var unsupported = new List<string>();
        if (policy.Actions.Count(x => x.Settings.Flag("AddInventoryCollection")) > InventoryCustomValue.MaximumResults)
            throw new InvalidDataException("Too many custom inventory scripts in one policy.");
        // Retired flags remain rejected in old published/cached policy snapshots;
        // silently ignoring them would report success for work that was never done.
        foreach (var key in new[] { "JoinDomain", "ImagePrepCleanup", "RunApplicationMonitor" })
            if (s.Flag(key))
                unsupported.Add(key);

        if (s.Number("RemoteAccess") != 0)
            unsupported.Add("RemoteAccess");

        foreach (var action in AllActions(policy))
        {
            if (action.Type == (int)ActionType.Script &&
                ScriptRuntime.ValidationError((ActionScriptType)action.Settings.Number("ScriptType")) is { } scriptError)
                throw new NotSupportedException(scriptError);

            if (action.Type is not ((int)ActionType.Printer or (int)ActionType.Software or (int)ActionType.Script
                or (int)ActionType.Command or (int)ActionType.File or (int)ActionType.Notification
                or (int)ActionType.DomainJoin or (int)ActionType.WindowsUpdate))
                unsupported.Add($"action type {action.Type}");

            if (action.Type == (int)ActionType.WindowsUpdate
                && action.Settings.Number("Mode", -1) is not ((int)WindowsUpdateMode.InstallAll or (int)WindowsUpdateMode.InstallApproved))
                unsupported.Add("unknown Windows Update mode");

            if (action.Settings.Flag("AddInventoryCollection")
                && (action.Type != (int)ActionType.Script || !policy.Actions.Contains(action)
                    || !(s.Flag("RunInventory") || s.Number("RunInventory") is >= 1 and <= 3)))
                throw new InvalidDataException("Custom inventory scripts must be assigned to a policy with Collect inventory enabled.");

            if (action.ConditionNextAction is { } target
                && (action.Settings.Flag("AddInventoryCollection")
                    || policy.Actions.Any(x => x.Guid == target && x.Settings.Flag("AddInventoryCollection"))))
                throw new InvalidDataException("Condition jumps cannot use custom inventory scripts.");

            if (action.Settings.Number("Timeout") < 0)
                throw new InvalidDataException("Invalid action timeout.");
        }

        if (unsupported.Count > 0)
            throw new NotSupportedException($"Policy requires features not yet available in this agent: {string.Join(
                ", ",
                unsupported.Distinct())}");
    }

    public static IEnumerable<AgentPolicyAction> AllActions(AgentPolicyContent policy)
    {
        if (policy.Condition is { } condition)
            yield return condition;

        foreach (var action in policy.Actions)
        {
            yield return action;

            if (action.Condition is { } nested)
                yield return nested;
        }
    }

    public async Task<PolicyProcessResult> RunAsync(
        AgentPolicyAction action,
        string directory,
        HttpClient client,
        Guid runId,
        CancellationToken ct,
        string? contentBasePath = null)
    {
        var s = action.Settings;

        if (action.Type == (int)ActionType.Script &&
            ScriptRuntime.ValidationError((ActionScriptType)s.Number("ScriptType")) is { } scriptError)
            throw new NotSupportedException(scriptError);

        // User-context actions are checked by the service before sending them over the authenticated execution pipe.
        if (action.Type == (int)ActionType.Software && s.Number("Type") == (int)SoftwareActionType.Pkg)
            throw new NotSupportedException("PKG installation requires the macOS agent.");
        if (!UserContext && WinGetFeatureGate.Applies(action) && await WinGetFeatureGate.CheckAsync(client, ct) is { } blocked)
            return blocked;

        if (UserContext)
            PolicyUserContext.ValidateAction(action);

        if (action.Type == (int)ActionType.WindowsUpdate)
        {
            List<WindowsUpdateIdentity>? approved = null;

            if (s.Number("Mode", -1) == (int)WindowsUpdateMode.InstallApproved)
            {
                var runPath = contentBasePath ?? $"api/agent/v1/policies/runs/{runId}";
                approved = await client.GetFromJsonAsync<List<WindowsUpdateIdentity>>(
                               $"{runPath}/windows-updates/{action.Guid}",
                               ct)
                           ?? throw new InvalidDataException("Missing Windows Update approval response.");
            }

            return await WindowsUpdateRunner.RunAsync(process, s, ct, approved);
        }

        if (action.Type == (int)ActionType.Software && s.Number("Type") == (int)SoftwareActionType.WinGet
            && s.Number("WinGetInstallType") == (int)WinGetInstallType.UpdateManaged)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (s.Number("Timeout") > 0)
                deadline.CancelAfter(TimeSpan.FromMinutes(s.Number("Timeout")));

            var updateToken = deadline.Token;
            var runPath = contentBasePath ?? $"api/agent/v1/policies/runs/{runId}";
            var updates = await client.GetFromJsonAsync<List<AgentPolicyAction>>(
                              $"{runPath}/managed-updates",
                              updateToken)
                          ?? throw new InvalidDataException("Missing managed update plan.");
            var output = new StringBuilder();
            var errors = new StringBuilder();
            var failed = false;
            var reboot = false;

            foreach (var update in updates)
            {
                if (update.Type != (int)ActionType.Software
                    || update.Settings.Number("Type") != (int)SoftwareActionType.WinGet
                    || update.Settings.Number("WinGetInstallType") != (int)WinGetInstallType.Update)
                    throw new InvalidDataException("Invalid managed update plan.");

                updateToken.ThrowIfCancellationRequested();
                var result = await RunAsync(update, directory, client, runId, updateToken, contentBasePath);
                AppendBounded(output, $"{update.Name}: exit {result.ExitCode}\n{result.Output}");

                if (!Succeeded(update, result))
                {
                    failed = true;
                    AppendBounded(errors, $"{update.Name}: {result.Error}");
                }

                reboot |= result.ExitCode == ProcessExitCodes.RebootRequired;
            }

            if (updates.Count == 0)
                output.AppendLine("No eligible managed applications in this device's assigned policies.");

            return new(failed ? 1 : reboot ? ProcessExitCodes.RebootRequired : 0, output.ToString(), errors.ToString());
        }

        if (action.Type == (int)ActionType.DomainJoin)
            return await DomainJoin.RunAsync(s, client, contentBasePath ?? $"api/agent/v1/policies/runs/{runId}", ct);

        if (action.Type == (int)ActionType.File)
        {
            var destination = Environment.ExpandEnvironmentVariables(s.Text("Destination"));

            if (!Path.IsPathFullyQualified(destination))
                throw new InvalidDataException("File destination must be absolute.");

            Directory.CreateDirectory(destination);

            foreach (var file in action.Files)
            {
                var source = Path.Combine(directory, file.Name);
                var target = Path.Combine(destination, file.Name);

                if (s.Flag("DecompressAfterCopy"))
                {
                    using var zip = ZipFile.OpenRead(source);

                    foreach (var entry in zip.Entries)
                    {
                        var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                        var root = $"{Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)}{Path.DirectorySeparatorChar}";

                        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':'))
                            throw new InvalidDataException("Archive entry escapes the destination.");

                        RejectReparsePoints(path);
                    }

                    zip.ExtractToDirectory(destination, s.Flag("OverwriteExisting"));
                }
                else
                {
                    RejectReparsePoints(target);

                    if (s.Flag("OverwriteExisting") || !File.Exists(target))
                        File.Copy(source, target, s.Flag("OverwriteExisting"));
                }

                ct.ThrowIfCancellationRequested();
            }

            return new(0, "", "");
        }

        if (action.Type == (int)ActionType.Notification)
        {
            ct.ThrowIfCancellationRequested();
            (Notifications ?? throw new InvalidOperationException("Portal notifications are not configured."))
                .Store(PortalNotice.Create(s, runId, action.Guid));

            return new(
                0,
                "Notification queued for the CloneDeploy portal. This does not confirm that a user has read it.",
                "");
        }

        var start = new ProcessStartInfo
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = s.Flag("RedirectStdOut") || s.Flag("AddInventoryCollection"),
            RedirectStandardError = s.Flag("RedirectStdError")
        };
        var working = s.Text("WorkingDirectory");

        if (working.Length > 0)
            start.WorkingDirectory = Environment.ExpandEnvironmentVariables(working);

        switch (action.Type)
        {
            case (int)ActionType.Command:
                var command = Environment.ExpandEnvironmentVariables(s.Text("Command"));
                start.FileName = WindowsTool("cmd.exe");
                start.Arguments = command.StartsWith(UnquotedCommandPrefix, StringComparison.Ordinal)
                    ? $"/d /s /c {command[UnquotedCommandPrefix.Length..]} {s.Text("Arguments")}"
                    : $"/d /s /c \"{Quote(command)} {s.Text("Arguments")}\"";
                break;
            case (int)ActionType.Script:
                var scriptType = s.Number("ScriptType");
                var extension = scriptType switch
                {
                    (int)ActionScriptType.Powershell or (int)ActionScriptType.Powershell7 => ".ps1",
                    (int)ActionScriptType.Batch => ".cmd",
                    _ => throw new InvalidDataException("Invalid script runtime.")
                };
                var scriptPath = Path.Combine(directory, $"script-{action.Guid.ToString("N")}{extension}");
                await File.WriteAllTextAsync(
                    scriptPath,
                    s.Text("ScriptContents"),
                    new UTF8Encoding(scriptType is (int)ActionScriptType.Powershell or (int)ActionScriptType.Powershell7),
                    ct);
                start.FileName = scriptType switch
                {
                    (int)ActionScriptType.Powershell => PowerShell,
                    (int)ActionScriptType.Powershell7 => FindPowerShell7(),
                    _ => WindowsTool("cmd.exe")
                };
                start.Arguments = scriptType switch
                {
                    (int)ActionScriptType.Powershell or (int)ActionScriptType.Powershell7 => $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(scriptPath)} {s.Text("Arguments")}",
                    _ => $"/d /s /c \"{Quote(scriptPath)} {s.Text("Arguments")}\""
                };
                break;
            case (int)ActionType.Software:
                if (s.Flag("Uninstall"))
                {
                    start.FileName = PowerShell;
                    start.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(SoftwareUninstall.Script(s)))}";
                }
                else if (s.Number("Type") == (int)SoftwareActionType.WinGet)
                {
                    var scope = WinGetScopeOptions.Resolve((WinGetScope)s.Number("WinGetScope"), s.Text("Arguments"), s.Text("AdditionalArguments"));
                    WinGetScopeOptions.ValidateContext(scope, (UserContext || s.Number("ManagedCredentialId") == 0) && WinGetPrerequisites.IsSystem);
                    start.RedirectStandardOutput = true;
                    start.RedirectStandardError = true;
                    start.FileName = UserContext
                        ? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Microsoft",
                            "WindowsApps",
                            "winget.exe")
                        : s.Number("ManagedCredentialId") == 0 && WinGetPrerequisites.IsSystem ? "winget.exe" : FindWinGet();
                    start.Arguments = WinGetOperation.Arguments(s);
                }
                else
                {
                    var installer = action.Files.Single(x => x.Id == s.Number("InstallerFileId"));
                    var installerPath = Path.Combine(directory, installer.Name);

                    if (s.Number("Type") == (int)SoftwareActionType.Msi)
                    {
                        start.FileName = WindowsTool("msiexec.exe");
                        var operation = s.Number("MsiInstallType") switch
                        {
                            (int)MsiInstallType.Install => "/i",
                            (int)MsiInstallType.Patch => "/p",
                            _ => throw new InvalidDataException("Invalid MSI operation.")
                        };
                        start.Arguments = $"{operation} {Quote(installerPath)} /qn /norestart {s.Text("Arguments")} {s.Text("AdditionalArguments")}";
                    }
                    else
                    {
                        start.FileName = installerPath;
                        start.Arguments = $"{s.Text("Arguments")} {s.Text("AdditionalArguments")}";
                    }
                }

                break;
            case (int)ActionType.Printer:
                return await PrinterActionRunner.RunAsync(s, UserContext, process, directory, ct);
            default:
                throw new NotSupportedException("Unsupported policy action.");
        }

        // Login and requesting-user execution always retain the user's identity,
        // even when the shared action specifies credentials for machine execution.
        var credentialId = UserContext ? 0 : s.Number("ManagedCredentialId");

        if (credentialId > 0)
        {
            var credential = await client.GetFromJsonAsync<AgentPolicyCredential>(
                                 $"{(contentBasePath ?? $"api/agent/v1/policies/runs/{runId}")}/credentials/{credentialId}",
                                 ct)
                             ?? throw new InvalidDataException("Missing run credential.");

            if (RunAs is null)
                throw new InvalidOperationException("Scheduled run-as execution is not configured.");

            if (action.Type == (int)ActionType.Software && !s.Flag("Uninstall")
                && s.Number("Type") == (int)SoftwareActionType.WinGet)
                return await WinGetOperation.RunAsync(
                    s,
                    start,
                    (command, token) => RunAs.RunAsync(command, credential, directory, s.Number("Timeout"), token),
                    ct);

            return await RunAs.RunAsync(start, credential, directory, s.Number("Timeout"), ct);
        }

        if (action.Type == (int)ActionType.Software && !s.Flag("Uninstall")
            && s.Number("Type") == (int)SoftwareActionType.WinGet)
        {
            if (!UserContext && WinGetPrerequisites.IsSystem)
            {
                var failure = await WinGetPrerequisites.PrepareAsync(start,
                    (command, token) => process.RunAsync(command, s.Number("Timeout"), token), ct);
                if (failure is not null) return failure;
            }
            return await WinGetOperation.RunAsync(
                s,
                start,
                (command, token) => process.RunAsync(command, s.Number("Timeout"), token),
                ct);
        }

        return await process.RunAsync(start, s.Number("Timeout"), ct);
    }

    private static void AppendBounded(StringBuilder text, string message)
    {
        const int outputLimit = 6000;
        const int entryLimit = 2000;
        var available = outputLimit - text.Length - Environment.NewLine.Length;

        if (available > 0)
            text.AppendLine(message[..Math.Min(message.Length, Math.Min(entryLimit, available))]);
    }

    public static bool Succeeded(AgentPolicyAction action, PolicyProcessResult result) =>
        action.Settings.Text("SuccessCodes", "0").Split(',').Any(x => int.TryParse(x.Trim(), out var code) && code == result.ExitCode);

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Policy file path contains a reparse point.");
    }

    public static string FindPowerShell7()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");

        return File.Exists(path) ? path : throw new FileNotFoundException("PowerShell 7 is not installed.");
    }

    public static string FindWinGet()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var path = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).Select(x => Path.Combine(x, "winget.exe")).FirstOrDefault(File.Exists)
            : null;

        return path ?? throw new FileNotFoundException("WinGet is not installed for this machine.");
    }
}
