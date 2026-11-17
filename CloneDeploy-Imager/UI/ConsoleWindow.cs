using System.Runtime.InteropServices;
using CloneDeploy_Imager.Diagnostics;
using Microsoft.Win32;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Requests a full-screen console window and records whether Windows accepted the request.
/// </summary>
internal static class ConsoleWindow
{
    private const int StandardOutputHandle = -11;
    private const uint FullScreenMode = 1;
    private const int MaximizeWindow = 3;

    public static void TryEnterFullScreen(DiagnosticLog log)
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return;

        try
        {
            using var winPe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            if (winPe is null) return;

            var output = GetStdHandle(StandardOutputHandle); // STD_OUTPUT_HANDLE
            if (output != IntPtr.Zero && output != new IntPtr(-1))
            {
                if (SetConsoleDisplayMode(output, FullScreenMode, IntPtr.Zero)) // CONSOLE_FULLSCREEN_MODE
                {
                    log.Write("Console entered full-screen mode.");
                    return;
                }
                log.Write($"Console full-screen mode unavailable (Win32 {Marshal.GetLastWin32Error()}); trying maximized window.");
            }

            var window = GetConsoleWindow();
            if (window == IntPtr.Zero) return;
            // ShowWindow returns the previous visibility, not whether maximizing succeeded.
            ShowWindow(window, MaximizeWindow); // SW_MAXIMIZE
            log.Write("Requested maximized console window.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Display sizing is optional; the imager must still start if it is unavailable.
            log.Failure("Console display setup", exception);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleDisplayMode(IntPtr consoleOutput, uint flags, IntPtr newScreenBufferDimensions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
