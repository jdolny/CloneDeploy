using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Enumerates interactive Windows sessions without reconnecting or logging on any user.</summary>
public static class DesktopSessions
{
    public sealed record Session(int Id, string UserName, string Connection, string State, bool Available);
    public static Session[] List()
    {
        if (!WTSEnumerateSessions(0, 0, 1, out var buffer, out var count)) throw new Win32Exception();
        try
        {
            var sessions = new List<Session>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SessionInfo>(buffer + i * Marshal.SizeOf<SessionInfo>());
                // Service and listener sessions cannot own an interactive user.
                // Keep idle/transitioning user sessions so they cannot look unattended.
                if (info.Id == 0 || info.State == 6) continue;
                var console = info.Id == DesktopNative.WTSGetActiveConsoleSessionId();
                var user = Query(info.Id, 5);
                var domain = Query(info.Id, 7);
                if (!console && string.IsNullOrWhiteSpace(user)) continue;
                sessions.Add(new(info.Id, string.IsNullOrEmpty(user) ? "" : string.IsNullOrEmpty(domain) ? user : domain + "\\" + user,
                    console ? "Console" : "RDP", info.State switch { 0 => "Active", 1 => "Connected", 4 => "Disconnected", 5 => "Idle", _ => "Unavailable" },
                    info.State == 0 || console && info.State == 1));
            }
            return sessions.OrderBy(x => x.Connection != "Console").ThenBy(x => x.Id).ToArray();
        }
        finally { WTSFreeMemory(buffer); }
    }
    public static bool IsAvailable(int sessionId)
    {
        if (sessionId <= 0) return false; // Zero is services; -1 means the caller's session to WTS.
        if (!WTSQuerySessionInformation(0, sessionId, 8, out var data, out var size)) return false;
        try
        {
            if (size < sizeof(int)) return false;
            var state = Marshal.ReadInt32(data);
            return state == 0 || sessionId == DesktopNative.WTSGetActiveConsoleSessionId() && state == 1;
        }
        finally { WTSFreeMemory(data); }
    }
    private static string Query(int id, int property)
    {
        if (!WTSQuerySessionInformation(0, id, property, out var data, out _)) throw new Win32Exception();
        try { return Marshal.PtrToStringUni(data) ?? ""; }
        finally { WTSFreeMemory(data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SessionInfo { public int Id; public nint Name; public int State; }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(nint server, int session, int property, out nint data, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint data);
}
