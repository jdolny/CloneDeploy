using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Transfers bounded Unicode text on request within the selected session's normal desktop.</summary>
internal static class DesktopClipboard
{
    private const uint UnicodeText = 13;
    internal static string Exchange(string? text)
    {
        if (text is not null && (text.Length > DesktopPipe.MaxClipboardCharacters || text.Contains('\0')))
            throw new InvalidDataException("Clipboard text is limited to 16,384 characters and cannot contain null characters.");
        using var desktop = new DesktopAttachment();
        if (!DesktopNative.IsDefaultDesktop()) throw new IOException("Clipboard is available only on the signed-in desktop, not the lock screen or UAC prompt.");
        var window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0); // HWND_MESSAGE
        if (window == 0) throw new Win32Exception();
        try
        {
            var opened = false;
            for (var attempt = 0; attempt < 5 && !(opened = OpenClipboard(window)); attempt++) Thread.Sleep(20);
            if (!opened) throw new IOException("The clipboard is busy. Try again.");
            try
            {
                if (text is null)
                {
                    var data = GetClipboardData(UnicodeText);
                    if (data == 0) return "";
                    var pointer = GlobalLock(data);
                    if (pointer == 0) throw new Win32Exception();
                    try
                    {
                        var characters = (int)Math.Min(GlobalSize(data) / 2, (nuint)(DesktopPipe.MaxClipboardCharacters + 1));
                        var value = Marshal.PtrToStringUni(pointer, characters) ?? "";
                        var end = value.IndexOf('\0');
                        if (end >= 0) value = value[..end];
                        if (value.Length > DesktopPipe.MaxClipboardCharacters) throw new IOException("Remote clipboard text exceeds 16,384 characters.");
                        return value;
                    }
                    finally { GlobalUnlock(data); }
                }
                var bytes = Encoding.Unicode.GetBytes(text + '\0');
                var memory = GlobalAlloc(2, (nuint)bytes.Length); // GMEM_MOVEABLE
                if (memory == 0) throw new Win32Exception();
                try
                {
                    var pointer = GlobalLock(memory);
                    if (pointer == 0) throw new Win32Exception();
                    try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                    finally { GlobalUnlock(memory); }
                    if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == 0) throw new Win32Exception();
                    memory = 0; // Windows owns it after SetClipboardData succeeds.
                    return "";
                }
                finally { if (memory != 0) GlobalFree(memory); }
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(window); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint style, string type, string title, uint windowStyle, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint data);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint data);
}
