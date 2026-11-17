using System.Runtime.InteropServices;
using System.Text;

namespace CloneDeploy_Agent.MacOS;

// macOS reports the foreground console user independently of any portal claim.
internal static class MacConsoleSession
{
    private const int ConsoleUserNameBufferCharacters = 1024;
    private const uint Utf8Encoding = 0x08000100;

    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport("/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration")]
    private static extern nint SCDynamicStoreCopyConsoleUser(nint store, out uint uid, out uint gid);
    [DllImport(CF)] private static extern void CFRelease(nint value);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, StringBuilder buffer, nint size, uint encoding);
    internal static (uint Uid, string Name)? Current()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var value = SCDynamicStoreCopyConsoleUser(0, out var uid, out _);
        if (value == 0) return null;
        try
        {
            var name = new StringBuilder(ConsoleUserNameBufferCharacters);
            if (uid == 0 || uid > int.MaxValue || !CFStringGetCString(value, name, ConsoleUserNameBufferCharacters, Utf8Encoding) || name.ToString() is "loginwindow" or "_mbsetupuser") return null;
            return (uid, name.ToString());
        }
        finally { CFRelease(value); }
    }
}
