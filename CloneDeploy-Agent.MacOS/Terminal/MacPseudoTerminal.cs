using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Darwin PTY with a separately spawned helper that execs zsh; never forks the managed daemon.</summary>
internal sealed class MacPseudoTerminal : IDisposable
{
    private const int TerminalNameBufferCharacters = 1024;
    private const int SetDescriptorFlags = 2;
    private const int CloseOnExec = 1;
    private const int SetStatusFlags = 4;
    private const int NonBlocking = 4;
    private const int ReadWrite = 2;
    private const uint SetControllingTerminal = 0x20007461;
    private const uint SetWindowSize = 0x80087467;
    private const int IoError = 5;
    private const int Interrupted = 4;
    private const int WouldBlock = 35;
    private const int IoRetryMilliseconds = 10;
    private const int StopSignal = 17;
    private const int KillSignal = 9;
    private const uint AllProcesses = 1;
    private const int ProcessListCapacity = 65536;

    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private readonly SafeFileHandle master;
    private readonly SafeFileHandle slave;
    private Process? process;
    private int closed;
    public int ProcessId => process!.Id;

    public MacPseudoTerminal(string? executable = null)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) throw new PlatformNotSupportedException();
        var name = new StringBuilder(TerminalNameBufferCharacters);
        var size = new WindowSize { Columns = 100, Rows = 30 };
        Require(openpty(out var m, out var s, name, 0, ref size));
        master = new SafeFileHandle(m, true);
        slave = new SafeFileHandle(s, true);
        try
        {
            Require(fcntl(master, SetDescriptorFlags, 0, 0, 0, 0, 0, 0, CloseOnExec)); // F_SETFD / FD_CLOEXEC
            Require(fcntl(slave, SetDescriptorFlags, 0, 0, 0, 0, 0, 0, CloseOnExec));
            Require(fcntl(master, SetStatusFlags, 0, 0, 0, 0, 0, 0, NonBlocking)); // F_SETFL / O_NONBLOCK (Darwin)
            var start = new ProcessStartInfo(executable ?? Path.Combine(AppContext.BaseDirectory, "CloneDeploy-Agent.MacOS"))
            {
                UseShellExecute = false,
                WorkingDirectory = "/var/root"
            };
            start.ArgumentList.Add("--terminal-child");
            start.ArgumentList.Add(name.ToString());
            // Do not pass service configuration or runtime injection variables into a root shell.
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
            start.Environment["HOME"] = "/var/root";
            start.Environment["USER"] = start.Environment["LOGNAME"] = "root";
            start.Environment["SHELL"] = "/bin/zsh";
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["LANG"] = "en_US.UTF-8";
            start.Environment["HISTFILE"] = "/dev/null";
            process = Process.Start(start) ?? throw new IOException("Could not start terminal helper.");
        }
        catch { Dispose(); throw; }
    }

    // Invoked before daemon initialization, only after the normal macOS/root checks.
    internal static int RunChild(string tty)
    {
        if (!tty.StartsWith("/dev/ttys", StringComparison.Ordinal) || tty.Length > 64
            || tty[9..].Length == 0 || tty[9..].Any(c => !char.IsAsciiDigit(c))) return 1;
        var fd = open(tty, ReadWrite); // O_RDWR
        if (fd < 0) return 1;
        try
        {
            if (isatty(fd) != 1 || setsid() < 0 || ioctl(fd, SetControllingTerminal, 0, 0, 0, 0, 0, 0, 0) < 0) return 1; // TIOCSCTTY
            for (var i = 0; i < 3; i++) if (dup2(fd, i) < 0) return 1;
        }
        finally { if (fd > 2) close(fd); }
        // zsh -f skips user startup files. exec replaces the helper, retaining its PID/session.
        var args = new[] { Marshal.StringToCoTaskMemUTF8("/bin/zsh"), Marshal.StringToCoTaskMemUTF8("-f"), Marshal.StringToCoTaskMemUTF8("-i"), nint.Zero };
        try { execv("/bin/zsh", args); return 1; }
        finally { foreach (var arg in args) if (arg != 0) Marshal.FreeCoTaskMem(arg); }
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = read(master, buffer, (nuint)buffer.Length);
            if (result >= 0) return (int)result;
            var error = Marshal.GetLastPInvokeError();
            if (error == IoError) return 0; // EIO is PTY EOF after the slave closes.
            if (error is not (Interrupted or WouldBlock)) throw new IOException("PTY read failed.", new Win32Exception(error));
            await Task.Delay(IoRetryMilliseconds, ct); // EINTR / EAGAIN; bounded cancellable I/O
        }
    }

    public async Task WriteAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var pending = offset == 0 ? buffer : buffer[offset..];
            var result = write(master, pending, (nuint)pending.Length);
            if (result > 0) { offset += (int)result; continue; }
            var error = Marshal.GetLastPInvokeError();
            if (result < 0 && error is not (Interrupted or WouldBlock)) throw new IOException("PTY write failed.", new Win32Exception(error));
            await Task.Delay(IoRetryMilliseconds, ct);
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!AgentTerminalLimits.ValidSize(columns, rows)) throw new ArgumentOutOfRangeException(nameof(columns));
        var size = new WindowSize { Columns = (ushort)columns, Rows = (ushort)rows };
        Require(ioctl(master, SetWindowSize, 0, 0, 0, 0, 0, 0, ref size)); // TIOCSWINSZ
    }

    public Task WaitForExitAsync(CancellationToken ct) => process!.WaitForExitAsync(ct);

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try
        {
            if (process is not null)
            {
                var pid = process.Id;
                // Freeze the shell before enumerating its jobs. Kill members of this
                // terminal session, including jobs in other foreground/background groups.
                if (getsid(pid) == pid) kill(pid, StopSignal); // SIGSTOP on Darwin
                var ids = new int[ProcessListCapacity];
                var bytes = proc_listpids(AllProcesses, 0, ids, ids.Length * sizeof(int)); // PROC_ALL_PIDS
                for (var i = 0; i < Math.Min(ids.Length, Math.Max(0, bytes) / sizeof(int)); i++)
                    if (ids[i] > 1 && ids[i] != pid && getsid(ids[i]) == pid) kill(ids[i], KillSignal);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
                {
                    if (!process.HasExited) kill(pid, KillSignal);
                }
            }
        }
        finally { slave.Dispose(); master.Dispose(); }
    }

    public void Dispose() { Close(); process?.Dispose(); }
    private static void Require(int result) { if (result < 0) throw new IOException("Native terminal operation failed.", new Win32Exception(Marshal.GetLastPInvokeError())); }
    [StructLayout(LayoutKind.Sequential)] private struct WindowSize { public ushort Rows, Columns, Width, Height; }
    [DllImport(Lib, SetLastError = true)] private static extern int openpty(out int master, out int slave, StringBuilder name, nint termios, ref WindowSize size);
    // Darwin ARM64 puts variadic arguments on the stack. Pad x2-x7 so the
    // final argument occupies the first stack slot; these declarations are ARM64-only.
    [DllImport(Lib, SetLastError = true)] private static extern int fcntl(SafeFileHandle fd, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint value);
    [DllImport(Lib, SetLastError = true)] private static extern nint read(SafeFileHandle fd, [Out] byte[] buffer, nuint count);
    [DllImport(Lib, SetLastError = true)] private static extern nint write(SafeFileHandle fd, byte[] buffer, nuint count);
    [DllImport(Lib, SetLastError = true)] private static extern int ioctl(SafeFileHandle fd, nuint command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, ref WindowSize size);
    [DllImport(Lib, SetLastError = true)] private static extern int ioctl(int fd, nuint command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint value);
    [DllImport(Lib, SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport(Lib)] private static extern int close(int fd);
    [DllImport(Lib)] private static extern int isatty(int fd);
    [DllImport(Lib)] private static extern int setsid();
    [DllImport(Lib)] private static extern int getsid(int pid);
    [DllImport(Lib)] private static extern int dup2(int oldFd, int newFd);
    [DllImport(Lib)] private static extern int execv(string path, nint[] argv);
    [DllImport(Lib)] private static extern int kill(int pid, int signal);
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_listpids(uint type, uint typeInfo, [Out] int[] buffer, int bufferSize);
}
