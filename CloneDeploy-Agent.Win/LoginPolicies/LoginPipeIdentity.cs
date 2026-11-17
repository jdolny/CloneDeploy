using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent;

/// <summary>Reads a pipe caller's Windows token and logon identity without trusting submitted usernames.</summary>
public static class LoginPipeIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 8;
    private const int TokenElevationInformation = 20;
    private const int TokenStatisticsInformation = 10;
    private const uint InteractiveLogon = 2;
    private const uint RemoteInteractiveLogon = 10;
    private const uint CachedInteractiveLogon = 11;
    private const uint CachedRemoteInteractiveLogon = 12;

    public static (string Sid, string Name, string LogonKey) Read(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            throw new Win32Exception();

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid); // Query limited information only.
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
            throw new Win32Exception();

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (!GetTokenElevation(token, TokenElevationInformation, out var elevated, sizeof(int), out _))
                throw new Win32Exception();

            if (identity.IsSystem || identity.User is null || elevated != 0)
                throw new UnauthorizedAccessException("Open the portal as the signed-in user, without elevation.");

            if (!GetTokenInformation(
                    token,
                    TokenStatisticsInformation,
                    out var stats,
                    Marshal.SizeOf<TokenStatistics>(),
                    out _))
                throw new Win32Exception();

            var status = LsaGetLogonSessionData(ref stats.AuthenticationId, out var data);

            if (status != 0)
                throw new Win32Exception((int)LsaNtStatusToWinError(status));

            try
            {
                var session = Marshal.PtrToStructure<LogonSession>(data);

                if (session.Session == 0
                    || session.LogonType is not (InteractiveLogon or RemoteInteractiveLogon or CachedInteractiveLogon or CachedRemoteInteractiveLogon))
                    throw new UnauthorizedAccessException("An interactive Windows logon is required.");

                return (identity.User.Value, identity.Name,
                    $"{identity.User.Value}:{stats.AuthenticationId.High:x8}{stats.AuthenticationId.Low:x8}:{session.LogonTime}");
            }
            finally
            {
                LsaFreeReturnBuffer(data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId;
        public long Expiration;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public Luid ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogonSession
    {
        public uint Size;
        public Luid LogonId;
        public UnicodeString UserName, Domain, AuthenticationPackage;
        public uint LogonType, Session;
        public nint Sid;
        public long LogonTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint access,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int info,
        out TokenStatistics stats,
        int length,
        out int returned);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenElevation(
        SafeAccessTokenHandle token,
        int info,
        out int elevated,
        int length,
        out int returned);

    [DllImport("secur32.dll")]
    private static extern uint LsaGetLogonSessionData(ref Luid logonId, out nint data);

    [DllImport("secur32.dll")]
    private static extern uint LsaFreeReturnBuffer(nint data);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);
}