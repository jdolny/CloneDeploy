using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CloneDeploy_Agent.UI;

/// <summary>Lets Windows Installer close and restart the portal in its existing user session, including tray-only startup.</summary>
internal sealed class PortalUpdateRestart : IDisposable
{
    private const uint PopupWindowStyle = 0x80000000;

    private const int QueryEndSession = 0x0011;
    private const int EndSession = 0x0016;
    private const long EndSessionCloseApp = 0x00000001;
    private const uint RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;
    private readonly HwndSource window;
    private readonly Action shutdown;

    internal PortalUpdateRestart(Action shutdown)
    {
        this.shutdown = shutdown;
        // A hidden top-level window receives Restart Manager messages even before
        // the lazily-created portal window exists. A message-only window would not.
        window = new HwndSource(new HwndSourceParameters("CloneDeploy portal update lifecycle")
        {
            WindowStyle = unchecked((int)PopupWindowStyle), // WS_POPUP; never WS_VISIBLE.
            Width = 0, Height = 0
        });
        window.AddHook(WindowMessage);
        var result = RegisterApplicationRestart("--tray", RestartNoCrash | RestartNoHang | RestartNoReboot);
        if (result < 0) Trace.TraceWarning(PortalText.Get("Portal.RegisterUpdateRestartFailed"), result);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((lParam.ToInt64() & EndSessionCloseApp) == 0) return 0;
        if (message == QueryEndSession)
        {
            handled = true;
            return 1; // Consent; don't exit until shutdown is confirmed.
        }
        if (message == EndSession && wParam != 0)
        {
            handled = true;
            // Return to Restart Manager before tearing down windows and the tray.
            window.Dispatcher.BeginInvoke(shutdown);
        }
        return 0;
    }

    public void Dispose() => window.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
}
