using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Attaches a synchronous capture/input operation to Windows' current input desktop, then restores its thread.</summary>
internal sealed class DesktopAttachment : IDisposable
{
    private const uint DesktopAccess = 0x02000000; // MAXIMUM_ALLOWED; does not change the desktop's ACL.
    private readonly nint original;
    private nint desktop;

    internal DesktopAttachment()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!DesktopSessions.IsAvailable(process.SessionId))
            throw new IOException("The selected Windows session disconnected.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !DesktopNative.IsDefaultDesktop())
            throw new IOException("Lock screens and UAC prompts require the installed agent running as SYSTEM.");
        original = GetThreadDesktop(GetCurrentThreadId());
        if (original == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        desktop = DesktopNative.OpenInputDesktop(0, false, DesktopAccess);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the active input desktop.");
        if (!SetThreadDesktop(desktop))
        {
            var error = Marshal.GetLastWin32Error();
            DesktopNative.CloseDesktop(desktop);
            desktop = 0;
            throw new Win32Exception(error, "Could not attach to the active input desktop.");
        }
    }

    public void Dispose()
    {
        if (desktop == 0) return;
        // No await may occur while attached: Windows desktop ownership is native-thread specific.
        if (!SetThreadDesktop(original))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the helper thread desktop.");
        DesktopNative.CloseDesktop(desktop);
        desktop = 0;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
}
