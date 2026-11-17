using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent.UI;

/// <summary>Verifies the pipe owner against Windows service status without opening the privileged service process.</summary>
internal static class PortalServiceIdentity
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 4;
    private const int ScStatusProcessInfo = 0;

    internal static void Verify(SafePipeHandle pipe, string serviceName = "CloneDeployAgent")
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pipeProcessId))
            throw NativeError(PortalText.Get("Connection.PipeOwnerUnknown"));
        // Only administrators can change the service registration. Querying its status
        // works for local users even when the SYSTEM process itself denies OpenProcess.
        if (pipeProcessId == 0 || pipeProcessId != GetRunningProcessId(serviceName))
            throw new IOException(PortalText.Get("Connection.UntrustedPipeOwner"));
    }

    internal static uint GetRunningProcessId(string serviceName)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw NativeError(PortalText.Get("Connection.WindowsServiceQueryFailed"));
        using var service = OpenService(manager, serviceName, ServiceQueryStatus);
        if (service.IsInvalid) throw NativeError(PortalText.Get("Connection.AgentServiceQueryFailed"));
        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status,
                Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw NativeError(PortalText.Get("Connection.AgentStatusReadFailed"));
        // Windows does not guarantee a valid PID while a service is starting or stopping.
        if (status.CurrentState != ServiceRunning || status.ProcessId == 0)
            throw new IOException(PortalText.Get("Connection.WaitForAgentStartup"));
        return status.ProcessId;
    }

    private static IOException NativeError(string operation)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new IOException(PortalText.Format("Connection.WindowsError", operation, error.Message, error.NativeErrorCode), error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level,
        out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
