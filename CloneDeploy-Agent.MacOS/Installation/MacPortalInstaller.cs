using System.Diagnostics;
namespace CloneDeploy_Agent.MacOS;
internal static class MacPortalInstaller
{
    public static async Task EnsureAsync(CancellationToken ct)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "install-portal.sh");
        if (!File.Exists(helper)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            start.ArgumentList.Add(helper);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start portal installer.");
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            if (process.ExitCode != 0) Console.Error.WriteLine("Portal installation failed. Device service continues; reinstall the Mac package to retry.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { Console.Error.WriteLine($"Portal installation unavailable ({ex.GetType().Name}). Device service continues."); }
    }
}
