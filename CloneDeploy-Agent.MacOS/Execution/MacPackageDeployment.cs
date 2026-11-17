using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed class MacPackageDeployment(AgentOptions options)
{
    public IMacScriptRunner Executor { get; init; } = new MacScriptRunner(options.StateDirectory);
    public static bool Supports(AgentPolicyAction action) => action.Type == (int)ActionType.Software && action.Settings.Number("Type", -1) == (int)SoftwareActionType.Pkg;

    internal static AgentPolicyFile Validate(AgentPolicyAction action)
    {
        var s = action.Settings;
        if (!Supports(action) || action.Condition is not null || s.Flag("Uninstall") || s.Number("ManagedCredentialId") != 0 ||
            !string.IsNullOrWhiteSpace(s.Text("Arguments")) || !string.IsNullOrWhiteSpace(s.Text("AdditionalArguments")))
            throw new NotSupportedException("PKG does not support Run As, uninstall, custom arguments, or unevaluated conditions.");
        var candidates = action.Files.Where(x => x.Id == s.Number("InstallerFileId")).ToArray();
        if (candidates.Length != 1 || !candidates[0].Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an attached .pkg installer.");
        if (s.Number("Timeout") < 0 || !s.Text("SuccessCodes", "0").Split(',').All(x => int.TryParse(x.Trim(), out _)))
            throw new InvalidDataException("Invalid package timeout or success codes.");
        return candidates[0];
    }

    internal static AgentPolicyAction InstallAction(AgentPolicyAction action, string path)
    {
        Validate(action);
        var s = action.Settings;
        return action with { Type = (int)ActionType.Script, Files = [], Settings = JsonSerializer.SerializeToElement(new
        {
            ScriptType = ActionScriptType.Bash,
            ScriptContents = "exec /usr/sbin/installer -pkg '" + path.Replace("'", "'\"'\"'") + "' -target /\n",
            Timeout = s.Number("Timeout"), SuccessCodes = s.Text("SuccessCodes", "0"),
            RedirectStdOut = s.Flag("RedirectStdOut"), RedirectStdError = s.Flag("RedirectStdError")
        }) };
    }

    public async Task<MacScriptResult> RunAsync(AgentPolicyAction action, HttpClient client, Guid runId,
        Guid device, string hash, string contentPath, CancellationToken ct)
    {
        var installer = Validate(action);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid package snapshot hash.");
        var policy = new AgentPolicyContent(0, Guid.NewGuid(), action.Name, 0, JsonSerializer.SerializeToElement(new { }), null, null, null, [action]);
        var snapshot = new AgentPolicySnapshot(1, hash, false, JsonSerializer.SerializeToElement(policy));
        var run = new PolicyRunEntry(runId, device, snapshot, ExecutionState.Running, DateTime.UtcNow, null, null, null, false);
        var cache = new PolicyCache(options);
        await cache.DownloadAsync(client, run, policy, ct, contentPath);
        var path = Path.Combine(cache.DirectoryFor(snapshot, action), installer.Name);
        if (!await PolicyCache.MatchesAsync(path, installer, ct)) throw new InvalidDataException("Package content changed before installation.");
        return await Executor.RunAsync(InstallAction(action, path), ct);
    }
}
