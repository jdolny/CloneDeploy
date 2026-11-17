using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Runs an explicit executable as root using the same bounded execution as scripts.</summary>
public sealed class MacCommandRunner(string directory) : IMacScriptRunner
{
    internal static AgentPolicyAction Prepare(AgentPolicyAction action)
    {
        if (action.Type != (int)ActionType.Command) throw new NotSupportedException("Not a command action.");
        var s = action.Settings;
        var command = s.Text("Command");
        if (!command.StartsWith('/') || command.Contains('\0') || command.Contains('\r') || command.Contains('\n'))
            throw new InvalidDataException("macOS commands require an absolute executable path, such as /usr/bin/id. Put arguments in the Arguments field.");
        // Quoting the executable and forwarding argv prevents paths/arguments becoming shell code.
        var settings = JsonSerializer.SerializeToElement(new
        {
            ScriptType = ActionScriptType.Bash,
            ScriptContents = "exec '" + command.Replace("'", "'\"'\"'") + "' \"$@\"\n",
            Arguments = s.Text("Arguments"), WorkingDirectory = s.Text("WorkingDirectory"),
            Timeout = s.Number("Timeout"), SuccessCodes = s.Text("SuccessCodes", "0"),
            RedirectStdOut = s.Flag("RedirectStdOut"), RedirectStdError = s.Flag("RedirectStdError"),
            ManagedCredentialId = s.Number("ManagedCredentialId"),
            IsCondition = s.Flag("IsCondition"), AddInventoryCollection = s.Flag("AddInventoryCollection")
        });
        var script = action with { Type = (int)ActionType.Script, Settings = settings };
        MacScriptRunner.Validate(script);
        return script;
    }

    public Task<MacScriptResult> RunAsync(AgentPolicyAction action, CancellationToken ct) =>
        new MacScriptRunner(directory).RunAsync(Prepare(action), ct);
}
