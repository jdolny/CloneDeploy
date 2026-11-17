using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed record MacScriptResult(bool Success, string Detail)
{
    public string Output { get; init; } = "";
}
public interface IMacScriptRunner
{
    Task<MacScriptResult> RunAsync(AgentPolicyAction action, CancellationToken ct);
}

public sealed class MacScriptRunner(string directory) : IMacScriptRunner
{
    private const int OutputBufferCharacters = 4096;
    private const int MaximumOutputCharacters = 6000;

    public static bool Supports(AgentPolicyAction action) => action.Type == (int)ActionType.Script &&
        action.Settings.Number("ScriptType", -1) is (int)ActionScriptType.Bash or (int)ActionScriptType.Zsh;

    internal static void Validate(AgentPolicyAction action)
    {
        if (!Supports(action)) throw new NotSupportedException("This script runtime is not supported on macOS.");
        var s = action.Settings;
        if (action.Condition is not null || action.Files.Count != 0 || s.Number("ManagedCredentialId") != 0)
            throw new NotSupportedException("macOS scripts do not support Run As credentials or attached files. Policy conditions are evaluated by the policy runner.");
        if (string.IsNullOrWhiteSpace(s.Text("ScriptContents")) || s.Number("Timeout") < 0)
            throw new InvalidDataException("Script contents and timeout are invalid.");
        if (!s.Text("SuccessCodes", "0").Split(',').All(x => int.TryParse(x.Trim(), out _)))
            throw new InvalidDataException("Invalid script success exit codes.");
    }

    internal static ProcessStartInfo Prepare(AgentPolicyAction action, string script, string work)
    {
        Validate(action);
        var s = action.Settings;
        var bash = s.Number("ScriptType") == (int)ActionScriptType.Bash;
        var start = new ProcessStartInfo(bash ? "/bin/bash" : "/bin/zsh")
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(s.Text("WorkingDirectory")) ? work : s.Text("WorkingDirectory"),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        if (!Path.IsPathFullyQualified(start.WorkingDirectory)) throw new InvalidDataException("Working directory must be absolute.");
        foreach (var option in bash ? new[] { "--noprofile", "--norc", "--" } : new[] { "-f", "--" }) start.ArgumentList.Add(option);
        start.ArgumentList.Add(script);
        foreach (var argument in ParseArguments(s.Text("Arguments"))) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin:/opt/homebrew/bin";
        start.Environment["HOME"] = "/var/root";
        foreach (var variable in new[] { "BASH_ENV", "ENV", "ZDOTDIR" }) start.Environment.Remove(variable);
        return start;
    }

    // Split literal arguments without evaluating substitutions or shell operators as root.
    internal static string[] ParseArguments(string text)
    {
        var args = new List<string>(); var word = new StringBuilder(); char quote = '\0'; var active = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0') throw new InvalidDataException("Arguments cannot contain null characters.");
            if (c == '\\' && quote != '\'')
            {
                if (++i == text.Length) throw new InvalidDataException("Arguments end with an incomplete escape.");
                word.Append(text[i]); active = true;
            }
            else if (quote != '\0') { if (c == quote) quote = '\0'; else word.Append(c); }
            else if (c is '\'' or '"') { quote = c; active = true; }
            else if (char.IsWhiteSpace(c)) { if (active) { args.Add(word.ToString()); word.Clear(); active = false; } }
            else { word.Append(c); active = true; }
        }
        if (quote != '\0') throw new InvalidDataException("Arguments contain an unmatched quote.");
        if (active) args.Add(word.ToString());
        return args.ToArray();
    }

    public async Task<MacScriptResult> RunAsync(AgentPolicyAction action, CancellationToken ct)
    {
        Validate(action);
        if (!OperatingSystem.IsMacOS() || geteuid() != 0) throw new NotSupportedException("macOS scripts must run in the root agent service.");
        var root = Path.Combine(directory, "script-runs");
        // Never stage privileged scripts through a user-controlled symbolic link.
        for (var parent = Path.GetFullPath(root); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Script staging path contains a symbolic link.");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var work = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var script = Path.Combine(work, "script.sh");
            await File.WriteAllTextAsync(script, action.Settings.Text("ScriptContents").Replace("\r\n", "\n"), new UTF8Encoding(false), ct);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var process = new Process { StartInfo = Prepare(action, script, work) };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(action.Settings.Number("Timeout") is > 0 and var minutes ? minutes : 60));
            if (!process.Start()) throw new IOException("Could not start script.");
            process.StandardInput.Close();
            async Task<string> Drain(StreamReader reader)
            {
                var text = new StringBuilder(); var buffer = new char[OutputBufferCharacters]; int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
                    if (text.Length < MaximumOutputCharacters) text.Append(buffer, 0, Math.Min(read, MaximumOutputCharacters - text.Length));
                return text.ToString();
            }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout; var error = await stderr;
                var success = action.Settings.Text("SuccessCodes", "0").Split(',').Any(x => int.Parse(x.Trim()) == process.ExitCode);
                return new(success, $"{action.Name}: exit {process.ExitCode}\n" +
                    (action.Settings.Flag("RedirectStdOut") ? output : "") +
                    (action.Settings.Flag("RedirectStdError") && error.Length > 0 ? "\nstderr:\n" + error : "")) { Output = output };
            }
            catch (Exception failure)
            {
                var timedOut = deadline.IsCancellationRequested && !ct.IsCancellationRequested;
                deadline.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(stdout, stderr); } catch { }
                ct.ThrowIfCancellationRequested();
                if (timedOut && failure is OperationCanceledException) return new(false, $"{action.Name}: script timed out.");
                throw;
            }
        }
        finally { Directory.Delete(work, true); }
    }
    [DllImport("libc")] private static extern uint geteuid();
}
