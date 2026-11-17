using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent.RemoteDesktop;

internal static class DesktopPipeTrust
{
    private const uint ServiceManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 4;
    private const uint ServiceOwnProcess = 0x10;
    private const int ProcessStatusInfo = 0;

    internal static void ValidateServer(NamedPipeClientStream pipe)
    {
        var manager = OpenSCManager(null, null, ServiceManagerConnect); // SC_MANAGER_CONNECT
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, "CloneDeployAgent", ServiceQueryStatus); // SERVICE_QUERY_STATUS
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!QueryServiceStatusEx(service, ProcessStatusInfo, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.CurrentState != ServiceRunning || status.ProcessId == 0 || (status.ServiceType & ServiceOwnProcess) == 0)
                    throw new UnauthorizedAccessException("The agent service is not running in its own process.");
                ValidateServerProcess(pipe, status.ProcessId);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static void ValidateServerProcess(NamedPipeClientStream pipe, uint expectedProcessId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualProcessId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (expectedProcessId == 0 || actualProcessId != expectedProcessId)
            throw new UnauthorizedAccessException("Desktop pipe server is not the installed agent service.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int infoLevel, out ServiceStatusProcess status, int bufferSize, out int bytesNeeded);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
