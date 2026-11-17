$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'request.json') -Raw | ConvertFrom-Json
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
public sealed class RunAsResult {
    public int ExitCode;
    public string Output = "";
    public string Error = "";
    public bool TimedOut;
}
public static class ScheduledRunAsHelper {
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses;
        public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    static IntPtr job;
    static async Task<string> Capture(StreamReader reader) {
        var text = new StringBuilder(); var buffer = new char[1024]; int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            if (text.Length < 4000) text.Append(buffer, 0, Math.Min(read, 4000 - text.Length));
        return text.ToString();
    }
    public static RunAsResult Execute(string executable, string arguments, string working, bool stdout, bool stderr, int seconds) {
        // Assign the wrapper before spawning, so children inherit the job without a launch race.
        // Its handle stays open until wrapper exit; Scheduler.Stop then kills the whole tree.
        job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000;
        if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(limits))
            || !AssignProcessToJobObject(job, GetCurrentProcess()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var timer = Stopwatch.StartNew();
        using (var process = new Process()) {
            process.StartInfo = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = stdout, RedirectStandardError = stderr
            };
            process.Start();
            var output = stdout ? Capture(process.StandardOutput) : Task.FromResult("");
            var error = stderr ? Capture(process.StandardError) : Task.FromResult("");
            int milliseconds = checked(seconds * 1000);
            if (!process.WaitForExit(milliseconds)
                || !Task.WaitAll(new Task[] { output, error }, Math.Max(0, milliseconds - (int)timer.ElapsedMilliseconds)))
                return new RunAsResult { ExitCode = -1, TimedOut = true, Error = "Run-as execution timed out." };
            return new RunAsResult { ExitCode = process.ExitCode, Output = output.Result, Error = error.Result };
        }
    }
}
'@
try {
    $result = [ScheduledRunAsHelper]::Execute($config.Executable, $config.Arguments, $config.WorkingDirectory,
        $config.Stdout, $config.Stderr, $config.TimeoutSeconds)
} catch {
    $result = @{ ExitCode = -1; Output = ''; Error = 'Could not launch the run-as process.'; TimedOut = $false }
}
$outputPath = Join-Path $PSScriptRoot 'output\result.json'
$json = $result | ConvertTo-Json -Compress
[IO.File]::WriteAllText(($outputPath + '.tmp'), $json, (New-Object Text.UTF8Encoding($false)))
[IO.File]::Move(($outputPath + '.tmp'), $outputPath)
exit $result.ExitCode
