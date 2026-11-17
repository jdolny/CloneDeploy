using System.Diagnostics;
using System.Text;

namespace CloneDeploy_Agent;

/// <summary>
/// Holds a program outcome and any output or errors it produced.
/// </summary>
public sealed record PolicyProcessResult(int ExitCode, string Output, string Error);

/// <summary>
/// Describes how to run a program and return its result.
/// </summary>
public interface IPolicyProcess
{
    Task<PolicyProcessResult> RunAsync(ProcessStartInfo start, int timeoutMinutes, CancellationToken ct);
}

/// <summary>
/// Runs a program, collects its output, and stops it if it takes too long or is cancelled.
/// </summary>
public sealed class PolicyProcess : IPolicyProcess
{
    private const int OutputBufferCharacters = 4096;
    private const int MaximumOutputCharacters = 4000;

    public async Task<PolicyProcessResult> RunAsync(
        ProcessStartInfo start,
        int timeoutMinutes,
        CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes == 0 ? ActionTimeout.DefaultMinutes : timeoutMinutes));

        if (!process.Start())
            throw new InvalidOperationException("Could not start policy process.");

        async Task<string> DrainAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[OutputBufferCharacters];
            int read;

            while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                if (text.Length < MaximumOutputCharacters)
                    text.Append(buffer, 0, Math.Min(read, MaximumOutputCharacters - text.Length));

            return text.ToString();
        }

        var output = start.RedirectStandardOutput ? DrainAsync(process.StandardOutput) : Task.FromResult("");
        var error = start.RedirectStandardError ? DrainAsync(process.StandardError) : Task.FromResult("");

        try
        {
            await process.WaitForExitAsync(timeout.Token);

            return new(process.ExitCode, await output, await error);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            try
            {
                await Task.WhenAll(output, error);
            }
            catch
            {
            }

            throw;
        }
    }
}