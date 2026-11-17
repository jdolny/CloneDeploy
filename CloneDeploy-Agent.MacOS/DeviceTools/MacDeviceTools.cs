using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

public static class MacSystemStatus
{
    private const int MaximumUserNameCharacters = 256;
    private const int MaximumUsers = 256;

    private const int MaximumProbeCharacters = 65536;
    private const int ProbeBufferCharacters = 4096;

    internal static string[] ParseUsers(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(x => x.Length >= 2).Select(x => x[0]).Where(x => x.Length <= MaximumUserNameCharacters)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaximumUsers).ToArray();
    public static async Task<string[]> UsersAsync(CancellationToken ct)
    {
        var users = ParseUsers(await ProbeAsync("/usr/bin/who", [], ct));
        var console = MacConsoleSession.Current();
        return console is { } user ? users.Append(user.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : users;
    }
    internal static long Uptime(string text, DateTimeOffset now)
    {
        var match = Regex.Match(text, @"\bsec\s*=\s*(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var seconds) || seconds <= 0 || seconds > now.ToUnixTimeSeconds())
            throw new InvalidDataException("Invalid macOS boot time.");
        return now.ToUnixTimeSeconds() - seconds;
    }
    public static async Task<string> ProbeAsync(string executable, string[] args, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.Environment["LC_ALL"] = "C";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new IOException("Could not start macOS status probe.");
        process.StandardInput.Close();
        async Task<string> Read(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[ProbeBufferCharacters]; int read;
            while ((read = await reader.ReadAsync(buffer, timeout.Token)) != 0)
            { if (result.Length + read > MaximumProbeCharacters) throw new IOException("Status probe output too large."); result.Append(buffer, 0, read); }
            return result.ToString();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new IOException("macOS status probe failed.");
            return await stdout;
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
    }
}

public sealed class MacDeviceTools
{
    private const int MaximumRememberedRequests = 128;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, AgentDeviceToolReply> completed = [];
    public Func<CancellationToken, Task<string[]>> Users { get; init; } = MacSystemStatus.UsersAsync;
    public Func<string, string[], CancellationToken, Task<string>> Probe { get; init; } = MacSystemStatus.ProbeAsync;
    public Func<bool, CancellationToken, Task> Schedule { get; init; } = ScheduleNativeAsync;
    private volatile bool pending;
    private bool pendingReboot;
    public bool Pending => pending;
    internal static ProcessStartInfo PowerCommand(bool reboot)
    {
        // shutdown forks a scheduler for +1. Do not redirect its inherited output and wait for the child's pipe to close.
        var start = new ProcessStartInfo("/sbin/shutdown") { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var arg in new[] { reboot ? "-r" : "-h", "+1", "CloneDeploy requested this power action. Save your work." }) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task ScheduleNativeAsync(bool reboot, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(PowerCommand(reboot)) ?? throw new IOException("Could not schedule power action.");
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new IOException("macOS rejected the power action.");
    }
    private async Task ScheduleCoreAsync(bool reboot, CancellationToken ct)
    {
        if (pending) { if (pendingReboot != reboot) throw new InvalidOperationException("A different power action is already scheduled."); return; }
        await Schedule(reboot, ct); pendingReboot = reboot; pending = true;
    }
    public async Task<bool> CompletePoliciesAsync(bool reboot, bool ifNoUsers, CancellationToken ct)
    {
        if (!reboot && !ifNoUsers) return false;
        await gate.WaitAsync(ct);
        try
        {
            if (!reboot && (await Users(ct)).Length != 0) return false;
            await ScheduleCoreAsync(true, ct); return true;
        }
        finally { gate.Release(); }
    }
    public async Task<AgentDeviceToolReply> ExecuteAsync(AgentDeviceToolRequest request, Guid device, CancellationToken ct = default)
    {
        AgentDeviceToolReply Reply(bool ok, string detail) => AgentDeviceToolReply.Result(request.RequestId, ok, detail);
        if (request.RequestId == Guid.Empty || request.DeviceGuid != device || request.ExpiresAtUtc <= DateTimeOffset.UtcNow || request.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return Reply(false, "Invalid or expired device tool request.");
        if (!await gate.WaitAsync(0, ct)) return Reply(false, "The device tools are busy. Try again shortly.");
        try
        {
            if (completed.TryGetValue(request.RequestId, out var prior)) return prior;
            if (request.Operation == AgentDeviceOperation.SystemStatus)
            {
                var now = DateTimeOffset.UtcNow;
                var uptime = MacSystemStatus.Uptime(await Probe("/usr/sbin/sysctl", ["-n", "kern.boottime"], ct), now);
                string[] users = []; string? error = null;
                try { users = await Users(ct); } catch (Exception) when (!ct.IsCancellationRequested) { error = "macOS could not enumerate logged-in users."; }
                return Reply(true, "") with { SystemStatus = new(now, uptime, users, error) };
            }
            if (request.Operation is not (AgentDeviceOperation.Reboot or AgentDeviceOperation.Shutdown)) return Reply(false, "This device tool is not supported on macOS.");
            await ScheduleCoreAsync(request.Operation == AgentDeviceOperation.Reboot, ct);
            var reply = Reply(true, "Power action scheduled in approximately one minute.");
            if (completed.Count >= MaximumRememberedRequests) completed.Remove(completed.Keys.First());
            completed[request.RequestId] = reply; return reply;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { Console.Error.WriteLine($"Mac device tool failed ({ex.GetType().Name})."); return Reply(false, "macOS could not complete the device tool request."); }
        finally { gate.Release(); }
    }
}
