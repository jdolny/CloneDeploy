using System.Runtime.InteropServices;
using System.Text;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Windows desktop capture and input declarations used by the session helper.</summary>
internal static class DesktopNative
{
    private const int DesktopNameBufferCharacters = 256;

    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder data, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] internal static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public int Size, Flags; public nint Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }

    internal static bool IsDefaultDesktop()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!DesktopSessions.IsAvailable(process.SessionId))
            throw new IOException("The selected Windows session disconnected.");
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not open the console input desktop.");
        try
        {
            var name = new StringBuilder(DesktopNameBufferCharacters);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the console input desktop.");
            return name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
}
