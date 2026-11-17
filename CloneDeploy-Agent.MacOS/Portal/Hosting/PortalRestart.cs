using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalRestart
{
    [DllImport("libc", SetLastError = true)]
    private static extern int execv(nint path, nint arguments);

    internal static void Execute(string path)
    {
        // Replace this process so launchd retains ownership; a separate child would
        // compete with KeepAlive and the single-instance lock. No --background:
        // the restarted portal opens its window for the user.
        var executable = Marshal.StringToCoTaskMemUTF8(path);
        var arguments = Marshal.AllocHGlobal(2 * nint.Size);
        try
        {
            Marshal.WriteIntPtr(arguments, executable);
            Marshal.WriteIntPtr(arguments, nint.Size, 0);
            execv(executable, arguments);
            // A successful exec never returns. Let launchd recover if it fails.
            Console.Error.WriteLine(PortalText.Format("Portal.RestartFailed", new Win32Exception(Marshal.GetLastPInvokeError()).Message));
            Environment.ExitCode = 1;
        }
        finally { Marshal.FreeHGlobal(arguments); Marshal.FreeCoTaskMem(executable); }
    }
}
