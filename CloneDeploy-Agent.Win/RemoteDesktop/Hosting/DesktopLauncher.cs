using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Starts the agent's desktop helper in the selected Windows session using a session-local SYSTEM token.</summary>
public static class DesktopLauncher
{
    private const int SecurityImpersonation = 2;
    private const int PrimaryToken = 1;
    private const uint CreateNoWindow = 0x08000000;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQueryAndDuplicate = 0x000A;
    private const uint MaximumAllowedAccess = 0x02000000;

    public static Process Start(string executable, string pipeName)
        => StartInSession(executable, pipeName, unchecked((int)WTSGetActiveConsoleSessionId()));

    public static Process StartInSession(string executable, string pipeName, int session)
    {
        if (!File.Exists(executable)) throw new IOException("The desktop helper executable is missing.");
        if (!DesktopSessions.IsAvailable(session)) throw new IOException("The selected Windows session is disconnected or unavailable.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
            throw new UnauthorizedAccessException("Desktop helper launch requires the agent service identity.");
        Process? sessionProcess = null;
        foreach (var candidate in Process.GetProcessesByName("winlogon"))
        {
            if (candidate.SessionId == session && sessionProcess is null) sessionProcess = candidate;
            else candidate.Dispose();
        }
        using var winlogon = sessionProcess ?? throw new IOException("The selected Windows session is unavailable.");
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, winlogon.Id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (processHandle == 0) throw new Win32Exception();
        nint token;
        try { if (!OpenProcessToken(processHandle, TokenQueryAndDuplicate, out token)) throw new Win32Exception(); } // TOKEN_QUERY | TOKEN_DUPLICATE
        finally { CloseHandle(processHandle); }
        try
        {
            if (!DuplicateTokenEx(token, MaximumAllowedAccess, 0, SecurityImpersonation, PrimaryToken, out var primary)) throw new Win32Exception(); // MAXIMUM_ALLOWED, impersonation, primary token
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                var command = new StringBuilder($"\"{executable}\" --remote-desktop-helper {pipeName}");
                if (!CreateProcessAsUser(primary, executable, command, 0, 0, false, CreateNoWindow, 0,
                    Path.GetDirectoryName(executable), ref startup, out var info)) throw new Win32Exception(); // CREATE_NO_WINDOW
                try
                {
                    var process = Process.GetProcessById((int)info.ProcessId);
                    try
                    {
                        // Retain a handle while the native creation handle still keeps the process alive.
                        // ExitCode otherwise cannot be read once this externally started process exits.
                        _ = process.SafeHandle;
                        return process;
                    }
                    catch { process.Dispose(); throw; }
                }
                finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
            }
            finally { CloseHandle(primary); }
        }
        finally { CloseHandle(token); }
    }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint token, uint access, nint attributes, int level, int type, out nint duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo info);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
}
