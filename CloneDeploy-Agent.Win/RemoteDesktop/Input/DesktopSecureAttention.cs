using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Sends Windows' secure attention sequence from the SYSTEM service into the selected interactive session.</summary>
public static class DesktopSecureAttention
{
    private const int SecurityImpersonation = 2;
    private const int ImpersonationToken = 2;
    private const int SecureAttentionServices = 1;
    private const int SecureAttentionServicesAndApplications = 3;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQueryAndDuplicate = 0x000A;
    private const uint MaximumAllowedAccess = 0x02000000;

    public static void Send(int sessionId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var ownProcess = Process.GetCurrentProcess();
        if (!identity.IsSystem || ownProcess.SessionId != 0) throw new IOException("Ctrl+Alt+Delete requires the installed SYSTEM agent service.");
        if (!DesktopSessions.IsAvailable(sessionId)) throw new IOException("The selected Windows session is unavailable.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var policy = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (policy?.GetValue("SoftwareSASGeneration") is not int allowed || allowed is not (SecureAttentionServices or SecureAttentionServicesAndApplications))
            throw new IOException("Windows policy does not allow service-generated Ctrl+Alt+Delete. Enable Services under Windows Logon Options → Disable or enable software Secure Attention Sequence.");
        var processes = Process.GetProcessesByName("winlogon");
        try
        {
            var process = processes.FirstOrDefault(x => x.SessionId == sessionId)
                ?? throw new IOException("The selected Windows logon session is unavailable.");
            using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
            if (processHandle.IsInvalid) throw new Win32Exception();
            if (!OpenProcessToken(processHandle, TokenQueryAndDuplicate, out var token)) throw new Win32Exception(); // TOKEN_QUERY | TOKEN_DUPLICATE
            using (token)
            {
                if (!DuplicateTokenEx(token, MaximumAllowedAccess, 0, SecurityImpersonation, ImpersonationToken, out var impersonation)) throw new Win32Exception();
                using (impersonation) WindowsIdentity.RunImpersonated(impersonation, () => SendSAS(false));
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("sas.dll")] private static extern void SendSAS(bool asUser);
}
