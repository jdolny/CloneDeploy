using System.ComponentModel;
using System.Diagnostics;

namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalRestart
{
    internal static void Execute(string path)
    {
        // A managed portal must exit fully. KeepAlive launches a fresh process,
        // and restart-open restores its window despite the --background argument.
        // Do not exec a new Cocoa application inside the old process identity.
        if (Environment.GetEnvironmentVariable("XPC_SERVICE_NAME") == "com.clonedeploy.agent.portal")
            return;

        // Finder/manual launches have no KeepAlive owner. LaunchServices starts a
        // fresh application after the UI and single-instance lock have shut down.
        var bundle = Directory.GetParent(path)?.Parent?.Parent?.FullName;
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(PortalText.Get("Portal.ExecutableUnavailable"));
            Environment.ExitCode = 1;
            return;
        }
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(bundle);
            using var process = Process.Start(start);
            if (process is null) throw new IOException("Could not launch the portal.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException($"open exited with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            Console.Error.WriteLine(PortalText.Format("Portal.RestartFailed", ex.Message));
            Environment.ExitCode = 1;
        }
    }
}
