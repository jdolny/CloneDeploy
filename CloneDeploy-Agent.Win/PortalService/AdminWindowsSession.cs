using System.Runtime.InteropServices;

namespace CloneDeploy_Agent;

internal static class AdminWindowsSession
{
    private const int ExtendedSessionInformation = 25;
    private const int SessionInfoLevel = 1;
    private const int MinimumSessionInfoBytes = 20;
    private const int SessionIdOffset = 8;
    private const int SessionStateOffset = 12;
    private const int SessionFlagsOffset = 16;
    private const int ActiveState = 0;
    private const int ConnectedState = 1;
    private const int UnlockedFlag = 1;

    public static bool IsUnlocked(int sessionId)
    {
        if (sessionId <= 0
            || !WTSQuerySessionInformation(
                0,
                sessionId,
                ExtendedSessionInformation,
                out var data,
                out var bytes))
            return false;

        try
        {
            // WTSINFOEXW's level-1 union is aligned to eight bytes because it contains LARGE_INTEGER.
            // Prefix: Level (0), SessionId (8), SessionState (12), SessionFlags (16).
            return bytes >= MinimumSessionInfoBytes && Marshal.ReadInt32(data) == SessionInfoLevel
                   && Marshal.ReadInt32( data, SessionIdOffset) == sessionId
                   &&
                   Marshal.ReadInt32(data, SessionStateOffset) is ActiveState or ConnectedState
                   && Marshal.ReadInt32( data, SessionFlagsOffset) == UnlockedFlag;
        }
        finally
        {
            WTSFreeMemory(data);
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        nint server,
        int session,
        int infoClass,
        out nint data,
        out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint data);
}