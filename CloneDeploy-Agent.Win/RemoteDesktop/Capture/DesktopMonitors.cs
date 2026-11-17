using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Discovers displays in the helper's session and maps selected-monitor input into virtual desktop coordinates.</summary>
internal static class DesktopMonitors
{
    private const int AbsoluteCoordinateMaximum = 65535;

    internal sealed record Monitor(string Id, int Left, int Top, int Width, int Height, bool Primary);
    internal static Monitor[] List()
    {
        var monitors = new List<Monitor>();
        if (!EnumDisplayMonitors(0, 0, (nint handle, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(handle, ref info)) return false;
            monitors.Add(new(info.Device, info.Bounds.Left, info.Bounds.Top, info.Bounds.Right - info.Bounds.Left,
                info.Bounds.Bottom - info.Bounds.Top, (info.Flags & 1) != 0));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate displays.");
        return monitors.OrderByDescending(x => x.Primary).ThenBy(x => x.Id).ToArray();
    }
    internal static Monitor Resolve(string? id)
    {
        var monitors = List();
        return monitors.FirstOrDefault(x => x.Id == id) ?? monitors.FirstOrDefault()
            ?? throw new DesktopUnavailableException("No display is available in this Windows session.");
    }
    internal static (int X, int Y) Map(Monitor monitor, double x, double y, int left, int top, int width, int height)
    {
        if (width <= 1 || height <= 1) throw new DesktopUnavailableException("The virtual display is unavailable.");
        return ((int)Math.Clamp(Math.Round((monitor.Left + x * (monitor.Width - 1) - left) * AbsoluteCoordinateMaximum / (width - 1)), 0, AbsoluteCoordinateMaximum),
            (int)Math.Clamp(Math.Round((monitor.Top + y * (monitor.Height - 1) - top) * AbsoluteCoordinateMaximum / (height - 1)), 0, AbsoluteCoordinateMaximum));
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Bounds, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
