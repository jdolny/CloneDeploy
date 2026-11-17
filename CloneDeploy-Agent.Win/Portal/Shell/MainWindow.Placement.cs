using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private const int DisplayChangeMessage = 0x007E;
    private const int SettingsChangeMessage = 0x001A;
    private const uint PreserveZOrderAndActivation = 0x0014;

    private bool fittingToScreen;
    private bool fitQueued;
    private void InitializeScreenPlacement()
    {
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ScreenChanged);
        DpiChanged += (_, _) => QueueScreenFit();
    }

    private nint ScreenChanged(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Display layout or work area (including taskbar) changed. Let Windows finish updating first.
        if (message is DisplayChangeMessage or SettingsChangeMessage) QueueScreenFit();
        return 0;
    }

    private void QueueScreenFit()
    {
        if (fitQueued || !IsVisible) return;
        fitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            fitQueued = false;
            FitToScreen();
        }));
    }

    internal void FitToScreen()
    {
        if (fittingToScreen || !IsVisible || WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds)) return;
        var screen = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = new Rect(screen.X, screen.Y, screen.Width, screen.Height);
        var fit = FitBounds(new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), area, dpi.DpiScaleX, dpi.DpiScaleY);
        fittingToScreen = true;
        try
        {
            // A small, heavily scaled display can be smaller than the usual minimum size.
            MinWidth = Math.Min(820, fit.Width / dpi.DpiScaleX);
            MinHeight = Math.Min(540, fit.Height / dpi.DpiScaleY);
            // Physical coordinates avoid scaling a secondary monitor's desktop origin incorrectly.
            SetWindowPos(handle, 0, (int)fit.X, (int)fit.Y, (int)fit.Width, (int)fit.Height, PreserveZOrderAndActivation); // No z-order or activation changes.
        }
        finally { fittingToScreen = false; }
    }

    internal static Rect FitBounds(Rect window, Rect workArea, double scaleX, double scaleY)
    {
        var marginX = Math.Min(Math.Ceiling(16 * scaleX), workArea.Width / 4);
        var marginY = Math.Min(Math.Ceiling(16 * scaleY), workArea.Height / 4);
        var width = Math.Min(window.Width, Math.Floor(workArea.Width - 2 * marginX));
        var height = Math.Min(window.Height, Math.Floor(workArea.Height - 2 * marginY));
        var x = window.Width > width ? workArea.Left + (workArea.Width - width) / 2 : window.Left;
        var y = window.Height > height ? workArea.Top + (workArea.Height - height) / 2 : window.Top;
        return new Rect(Math.Ceiling(Math.Clamp(x, workArea.Left + marginX, workArea.Right - marginX - width)),
            Math.Ceiling(Math.Clamp(y, workArea.Top + marginY, workArea.Bottom - marginY - height)), width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out ScreenRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
