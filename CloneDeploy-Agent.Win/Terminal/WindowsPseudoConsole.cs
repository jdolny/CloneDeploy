using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Agent;

/// <summary>Hosts a Windows shell inside ConPTY and a job that terminates its descendants on close.</summary>
internal sealed class WindowsPseudoConsole : IDisposable
{
    private const int PipeBufferBytes = 4096;
    private const int MinimumWindowsBuild = 17763;

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint UseStandardHandles = 0x00000100;
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;
    private static readonly nint PseudoConsoleAttribute = 0x00020016;
    private nint console;
    private readonly object gate = new();
    private SafeFileHandle? job;
    private Process? process;
    public FileStream Input { get; private set; } = null!;
    public FileStream Output { get; private set; } = null!;
    public int ProcessId => process!.Id;
    public Task WaitForExitAsync(CancellationToken ct) => process!.WaitForExitAsync(ct);

    public WindowsPseudoConsole(AgentTerminalShell shell)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild))
            throw new PlatformNotSupportedException("Remote terminal requires Windows 10 1809 or Windows Server 2019 or later.");
        if (shell is not (AgentTerminalShell.PowerShell or AgentTerminalShell.CommandPrompt))
            throw new ArgumentOutOfRangeException(nameof(shell));

        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint attributes = 0;
        var initialized = false;
        ProcessInformation child = default;
        try
        {
            Require(CreatePipe(out inputRead, out inputWrite, 0, 0));
            Require(CreatePipe(out outputRead, out outputWrite, 0, 0));
            Input = new FileStream(inputWrite, FileAccess.Write, PipeBufferBytes, false);
            Output = new FileStream(outputRead, FileAccess.Read, PipeBufferBytes, false);
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new(100, 30), inputRead, outputWrite, 0, out console));

            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Require(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            initialized = true;
            Require(UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, (nuint)IntPtr.Size, 0, 0));

            job = CreateJobObjectW(0, null);
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new JobLimits { Basic = new() { LimitFlags = KillOnJobClose } };
            Require(SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            var executable = shell == AgentTerminalShell.CommandPrompt
                ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var arguments = shell == AgentTerminalShell.CommandPrompt ? "/d" : "-NoLogo -NoProfile -NoExit";
            // Null standard handles prevent redirected service/console handles from overriding ConPTY.
            var startup = new StartupInfoEx
            {
                Startup = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = UseStandardHandles },
                Attributes = attributes
            };
            Require(CreateProcessW(executable, new StringBuilder($"\"{executable}\" {arguments}"), 0, 0, false,
                ExtendedStartupInfoPresent | CreateSuspended, 0, Environment.SystemDirectory, ref startup, out child));
            Require(AssignProcessToJobObject(job, child.Process));
            process = Process.GetProcessById((int)child.ProcessId);
            _ = process.SafeHandle;
            if (ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception();
        }
        catch
        {
            if (child.Process != 0) _ = TerminateProcess(child.Process, 1);
            inputRead?.Dispose();
            outputWrite?.Dispose();
            // Drain any final ConPTY frame while closing, including failed shell startup.
            var drain = Output is null ? Task.CompletedTask : Task.Run(() => { try { Output.CopyTo(Stream.Null); } catch (IOException) { } });
            Close();
            drain.GetAwaiter().GetResult();
            Dispose();
            throw;
        }
        finally
        {
            if (child.Thread != 0) CloseHandle(child.Thread);
            if (child.Process != 0) CloseHandle(child.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            inputRead?.Dispose();
            outputWrite?.Dispose();
            if (Input is null) inputWrite?.Dispose();
            if (Output is null) outputRead?.Dispose();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!AgentTerminalLimits.ValidSize(columns, rows))
            throw new IOException("Invalid terminal dimensions.");
        lock (gate)
        {
            if (console == 0) throw new ObjectDisposedException(nameof(WindowsPseudoConsole));
            Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, new((short)columns, (short)rows)));
        }
    }

    // The caller must keep draining Output until this returns: ConPTY can write a final frame.
    public void Close()
    {
        lock (gate)
        {
            job?.Dispose();
            var handle = Interlocked.Exchange(ref console, 0);
            if (handle != 0) ClosePseudoConsole(handle);
        }
    }

    public void Dispose()
    {
        Close();
        Input?.Dispose();
        Output?.Dispose();
        process?.Dispose();
    }

    private static void Require(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes;
        public nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint security, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(nint console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
