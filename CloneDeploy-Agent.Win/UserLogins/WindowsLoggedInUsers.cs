using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent;

/// <summary>
/// Asks Windows which users are currently signed in, including disconnected sessions.
/// </summary>
public static class WindowsLoggedInUsers
{
    private const int ListenerState = 6;
    private const int UserNameInformation = 5;
    private const int DomainNameInformation = 7;

    // Include disconnected sessions: disconnecting or locking is not logging out.
    public static IReadOnlyCollection<string> Read()
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in EnumerateSessions())
        {
            if (session.Id == 0 || session.State == ListenerState)
                continue; // Services and listeners.
            var user = Query(session.Id, UserNameInformation);

            if (string.IsNullOrWhiteSpace(user))
                continue;

            var domain = Query(session.Id, DomainNameInformation);
            users.Add(string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}");
        }

        return users;
    }

    // Preserve the reboot check's broader scope: any named session prevents a restart.
    // Inspect every session so an enumeration/query failure still prevents the reboot.
    internal static bool HasLoggedOnUsers()
    {
        var found = false;

        foreach (var session in EnumerateSessions())
            found |= !string.IsNullOrWhiteSpace(Query(session.Id, UserNameInformation));

        return found;
    }

    private static IEnumerable<Session> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            for (var i = 0; i < count; i++)
                yield return Marshal.PtrToStructure<Session>(IntPtr.Add(buffer, i * Marshal.SizeOf<Session>()));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string Query(int session, int info)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, info, out var buffer, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public int Id;
        public IntPtr StationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server,
        int reserved,
        int version,
        out IntPtr sessions,
        out int count);

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        int session,
        int info,
        out IntPtr buffer,
        out int bytes);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr buffer);
}