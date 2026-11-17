using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent;

/// <summary>Prepares the machine WinGet installation only for LocalSystem actions.</summary>
public static class WinGetPrerequisites
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static bool IsSystem { get { using var identity = WindowsIdentity.GetCurrent(); return identity.IsSystem; } }

    public static async Task<PolicyProcessResult?> PrepareAsync(ProcessStartInfo start,
        Func<ProcessStartInfo, CancellationToken, Task<PolicyProcessResult>> execute, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var resource = typeof(WinGetPrerequisites).Assembly.GetManifestResourceStream("CloneDeploy_Agent.Prepare-WinGet.ps1")
                ?? throw new InvalidOperationException("WinGet prerequisite resource is missing.");
            using var reader = new StreamReader(resource);
            var script = await reader.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            PolicyProcessResult result;
            try
            {
                result = await execute(new ProcessStartInfo(PolicyActionRunner.PowerShell,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                    { WorkingDirectory = start.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true }, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(1, "", "WinGet prerequisite preparation timed out. Installation of the requested application was not attempted.");
            }
            if (result.ExitCode != 0)
                return result with { Error = "WinGet prerequisite preparation failed; the requested application was not started.\n" + result.Error };
            try
            {
                var prepared = JsonSerializer.Deserialize<Prepared>(result.Output) ?? throw new JsonException();
                if (!Path.IsPathFullyQualified(prepared.FileName) || prepared.Directories.Length == 0 ||
                    prepared.Directories.Any(x => !Path.IsPathFullyQualified(x))) throw new JsonException();
                start.FileName = prepared.FileName;
                start.Environment.TryGetValue("PATH", out var inheritedPath);
                start.Environment["PATH"] = string.Join(';', prepared.Directories) + ";" + inheritedPath;
                return null;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
            {
                return new(1, result.Output, "WinGet preparation returned an invalid launch configuration. The requested application was not started.");
            }
        }
        finally { gate.Release(); }
    }
    private sealed record Prepared(string FileName, string[] Directories);
}
