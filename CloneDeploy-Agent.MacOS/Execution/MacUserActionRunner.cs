using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

// Downloads stay in the daemon; every destination write and executable runs with the console user's credentials.
public interface IMacUserActionRunner : IMacScriptRunner
{
    Task<MacScriptResult> RunFilesAsync(AgentPolicyAction action, AgentOptions options, HttpClient client, Guid runId, Guid device, string hash, CancellationToken ct);
}

public sealed class MacUserActionRunner(uint uid) : IMacUserActionRunner
{
    private const int AccountLookupBufferBytes = 65536;
    private const int OutputBufferCharacters = 4096;
    private const int MaximumOutputCharacters = 6000;
    private const int ExitPollMilliseconds = 1000;

    internal static uint ParseUid(string localUser) => localUser.StartsWith("macos:", StringComparison.Ordinal) &&
        uint.TryParse(localUser.AsSpan(6), out var value) && value is > 0 and <= int.MaxValue
        ? value : throw new InvalidDataException("Missing local Mac login identity.");

    internal static ProcessStartInfo Prepare(AgentPolicyAction action, uint uid, string name, string home)
    {
        if (uid is 0 or > int.MaxValue) throw new InvalidDataException("Login actions require a non-root user.");
        MacScriptRunner.Validate(action);
        var shell = action.Settings.Number("ScriptType") == (int)ActionScriptType.Bash ? "/bin/bash" : "/bin/zsh";
        var work = action.Settings.Text("WorkingDirectory");
        if (string.IsNullOrWhiteSpace(work)) work = home;
        if (!work.StartsWith('/') || work.Contains('\0')) throw new InvalidDataException("Working directory must be absolute.");
        var start = new ProcessStartInfo("/bin/launchctl") { WorkingDirectory = "/", UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var arg in new[] { "asuser", uid.ToString(), "/usr/bin/sudo", "-n", "-H", "-u", "#" + uid, "--", "/usr/bin/env", "-i",
            "HOME=" + home, "USER=" + name, "LOGNAME=" + name, "PATH=/usr/bin:/bin:/usr/sbin:/sbin:/usr/local/bin:/opt/homebrew/bin", shell }) start.ArgumentList.Add(arg);
        foreach (var arg in shell == "/bin/bash" ? new[] { "--noprofile", "--norc", "-s", "--" } : new[] { "-f", "-s", "--" }) start.ArgumentList.Add(arg);
        foreach (var arg in MacScriptRunner.ParseArguments(action.Settings.Text("Arguments"))) start.ArgumentList.Add(arg);
        return start;
    }
    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private void RequireConsole()
    {
        if (!OperatingSystem.IsMacOS() || geteuid() != 0 || MacConsoleSession.Current()?.Uid != uid)
            throw new InvalidOperationException("The policy's local user is no longer the active console user.");
    }
    public async Task<MacScriptResult> RunAsync(AgentPolicyAction action, CancellationToken ct)
    {
        RequireConsole();
        if (action.Type == (int)ActionType.Command) action = MacCommandRunner.Prepare(action);
        var buffer = Marshal.AllocHGlobal(AccountLookupBufferBytes);
        string name, home;
        try
        {
            if (getpwuid_r(uid, out var entry, buffer, AccountLookupBufferBytes, out var found) != 0 || found == 0) throw new IOException("Local user account unavailable.");
            name = Marshal.PtrToStringUTF8(entry.Name) ?? throw new IOException("Missing local username.");
            home = Marshal.PtrToStringUTF8(entry.Home) ?? throw new IOException("Missing local home directory.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        using var process = new Process { StartInfo = Prepare(action, uid, name, home) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(action.Settings.Number("Timeout") is > 0 and var minutes ? minutes : 60));
        if (!process.Start()) throw new IOException("Could not start login action.");
        async Task<string> Drain(StreamReader reader)
        {
            var output = new StringBuilder(); var buffer = new char[OutputBufferCharacters]; int read;
            while ((read = await reader.ReadAsync(buffer, deadline.Token)) > 0)
                if (output.Length < MaximumOutputCharacters) output.Append(buffer, 0, Math.Min(read, MaximumOutputCharacters - output.Length));
            return output.ToString();
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        try
        {
            var work = action.Settings.Text("WorkingDirectory");
            if (string.IsNullOrWhiteSpace(work)) work = home;
            await process.StandardInput.WriteAsync(("cd -- " + Quote(work) + " || exit 1\n" + action.Settings.Text("ScriptContents").Replace("\r\n", "\n") + "\n").AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(deadline.Token);
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(ExitPollMilliseconds, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
                RequireConsole();
            }
            await exited;
            var output = await stdout; var error = await stderr;
            var success = action.Settings.Text("SuccessCodes", "0").Split(',').Any(x => int.Parse(x.Trim()) == process.ExitCode);
            return new(success, $"{action.Name} (user {name}): exit {process.ExitCode}\n" +
                (action.Settings.Flag("RedirectStdOut") ? output : "") + (action.Settings.Flag("RedirectStdError") ? error : "")) { Output = output };
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            deadline.Cancel();
            try { await Task.WhenAll(stdout, stderr); } catch { }
            ct.ThrowIfCancellationRequested();
            return new(false, $"{action.Name}: login action failed ({ex.Message}).");
        }
    }

    public async Task<MacScriptResult> RunFilesAsync(AgentPolicyAction action, AgentOptions options, HttpClient client,
        Guid runId, Guid device, string hash, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Login file actions require macOS.");
        RequireConsole();
        if (action.Type != (int)ActionType.File && !MacPackageDeployment.Supports(action)) throw new NotSupportedException("Unsupported login file action.");
        var policy = new AgentPolicyContent(0, Guid.NewGuid(), action.Name, 0, JsonSerializer.SerializeToElement(new { }), null, null, null, [action]);
        var snapshot = new AgentPolicySnapshot(1, hash, false, JsonSerializer.SerializeToElement(policy));
        var run = new PolicyRunEntry(runId, device, snapshot, ExecutionState.Running, DateTime.UtcNow, null, null, null, false);
        var cache = new PolicyCache(options);
        await cache.DownloadAsync(client, run, policy, ct);
        // Root-owned staging directories cannot be replaced or redirected by the user. Only this UID can read the files.
        var stage = Path.Combine("/private/tmp", "clonedeploy-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            AgentPolicyAction script;
            if (MacPackageDeployment.Supports(action))
            {
                var file = MacPackageDeployment.Validate(action);
                var source = Path.Combine(cache.DirectoryFor(snapshot, action), file.Name);
                if (!await PolicyCache.MatchesAsync(source, file, ct)) throw new IOException("Cached package changed.");
                File.Copy(source, Path.Combine(stage, file.Name));
                script = MacPackageDeployment.InstallAction(action, Path.Combine(stage, file.Name));
            }
            else
            {
                await MacFileDeployment.CopyAsync(action, cache.DirectoryFor(snapshot, action), stage, ct);
                script = FileCopyAction(action, stage);
            }
            foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead);
                if (chown(file, uid, uint.MaxValue) != 0) throw new IOException("Could not grant login user access to staged content.");
            }
            foreach (var path in Directory.GetDirectories(stage, "*", SearchOption.AllDirectories).Prepend(stage))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return await RunAsync(script, ct);
        }
        finally { Directory.Delete(stage, true); }
    }
    internal static AgentPolicyAction FileCopyAction(AgentPolicyAction action, string stage)
    {
        var destination = action.Settings.Text("Destination");
        if (!destination.StartsWith('/') || destination.Contains('\0')) throw new InvalidDataException("macOS file destination must be absolute.");
        var body = new StringBuilder("set -e\ntemporary=\ntrap '[ -z \"$temporary\" ] || /bin/rm -f -- \"$temporary\"' EXIT\n");
        body.AppendLine("/bin/mkdir -p -- " + Quote(destination));
        foreach (var folder in Directory.GetDirectories(stage, "*", SearchOption.AllDirectories))
            body.AppendLine("/bin/mkdir -p -- " + Quote(Path.Combine(destination, Path.GetRelativePath(stage, folder))));
        foreach (var file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            var target = Quote(Path.Combine(destination, Path.GetRelativePath(stage, file)));
            if (!action.Settings.Flag("OverwriteExisting") && action.Settings.Flag("DecompressAfterCopy")) body.AppendLine("if [ -e " + target + " ]; then echo 'Destination already exists' >&2; exit 1; fi");
            if (!action.Settings.Flag("OverwriteExisting")) body.AppendLine("if [ ! -e " + target + " ]; then");
            body.AppendLine("temporary=" + Quote(Path.Combine(destination, Path.GetRelativePath(stage, file)) + "." + Guid.NewGuid().ToString("N") + ".partial"));
            body.AppendLine("/bin/cp " + Quote(file) + " \"$temporary\"");
            body.AppendLine("/bin/chmod u+rw \"$temporary\"");
            body.AppendLine("/bin/mv -f \"$temporary\" " + target);
            if (!action.Settings.Flag("OverwriteExisting")) body.AppendLine("fi");
        }
        return action with { Type = (int)ActionType.Script, Files = [], Settings = JsonSerializer.SerializeToElement(new {
            ScriptType = ActionScriptType.Bash, ScriptContents = body.ToString(), SuccessCodes = "0", RedirectStdOut = true, RedirectStdError = true }) };
    }
    [StructLayout(LayoutKind.Sequential)] private struct Passwd
    { public nint Name, Password; public uint Uid, Gid; public long Change; public nint Class, Gecos, Home, Shell; public long Expire; public int Fields; }
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern int getpwuid_r(uint uid, out Passwd entry, nint buffer, nuint size, out nint result);
    [DllImport("libc", SetLastError = true)] private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}
