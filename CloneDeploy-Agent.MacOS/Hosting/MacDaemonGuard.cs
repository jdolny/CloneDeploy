using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace CloneDeploy_Agent.MacOS;

internal static class MacDaemonGuard
{
    private const int LocalSocketLevel = 0;
    private const int LocalPeerProcessId = 2;

    private const int ProcessPathBufferBytes = 4096;

    internal const string Executable = "/Library/Application Support/CloneDeploy/Agent/bin/CloneDeploy-Agent.MacOS";
    internal static void RequireDaemon()
    {
        if (!OperatingSystem.IsMacOS() || geteuid() != 0 || getppid() != 1 ||
            !IsInstalledProcess(Environment.ProcessId))
            throw new UnauthorizedAccessException("Start the installed agent through launchd.");
    }
    internal static void RequireTerminalParent()
    {
        if (!IsInstalledProcess(getppid())) throw new UnauthorizedAccessException("Terminal helper requires the installed agent parent.");
    }
    internal static bool IsInstalledProcess(int pid)
    {
        var buffer = new byte[ProcessPathBufferBytes];
        var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        if (length <= 0) return false;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? length : end) == Executable;
    }
    internal static void VerifyPeer(Socket socket)
    {
        uint length = sizeof(int);
        // Darwin SOL_LOCAL / LOCAL_PEERPID, from Apple's bsd/sys/un.h.
        if (!OperatingSystem.IsMacOS() || getpeereid(socket.Handle.ToInt32(), out var uid, out _) != 0 || uid != 0 ||
            getsockopt(socket.Handle.ToInt32(), LocalSocketLevel, LocalPeerProcessId, out var pid, ref length) != 0 || length != sizeof(int) || !IsInstalledProcess(pid))
            throw new IOException("Could not verify the installed device service.");
    }
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern int getppid();
    [DllImport("libproc")] private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    [DllImport("libc")] private static extern int getpeereid(int socket, out uint uid, out uint gid);
    [DllImport("libc")] private static extern int getsockopt(int socket, int level, int option, out int value, ref uint length);
}
