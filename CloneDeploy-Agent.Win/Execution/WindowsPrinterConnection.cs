using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace CloneDeploy_Agent;

public sealed class WindowsPrinterConnection : IPrinterConnection
{
    public bool Exists(string path, bool userContext)
    {
        if (!userContext)
        {
            // /ga registers a connection for future logons; EnumPrinters in SYSTEM's
            // profile cannot verify that registration. Read it without modifying it.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var connections = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Connections");
            return connections?.GetSubKeyNames().Contains(path.Replace('\\', ','), StringComparer.OrdinalIgnoreCase) == true;
        }
        const uint connectionsFlag = 4;
        if (!EnumPrinters(connectionsFlag, null, 4, IntPtr.Zero, 0, out var needed, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 122) throw new Win32Exception(error);
        }
        if (needed == 0) return false;
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(connectionsFlag, null, 4, buffer, needed, out _, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var size = Marshal.SizeOf<PrinterInfo4>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, checked((int)i * size)));
                if (string.Equals(Marshal.PtrToStringUni(info.PrinterName), path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public Task InstallAsync(string path, bool userContext, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!userContext) return PrintUiAsync("/ga", path, ct);
        if (!AddPrinterConnection(path)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, bool userContext, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!userContext) return PrintUiAsync("/gd", path, ct);
        if (!DeletePrinterConnection(path)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    private static async Task PrintUiAsync(string operation, string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(PolicyActionRunner.WindowsTool("rundll32.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(PolicyActionRunner.WindowsTool("printui.dll") + ",PrintUIEntry");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add("/n");
        start.ArgumentList.Add(path);
        var result = await new PolicyProcess().RunAsync(start, 0, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Computer printer registration failed ({result.ExitCode}): {result.Error}");
        // The caller verifies registration even when PrintUI returns zero.
    }

    public void SetDefault(string path)
    {
        if (!SetDefaultPrinter(path)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public string? GetDefault()
    {
        uint size = 0;
        if (!GetDefaultPrinter(null, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return null;
            if (error != 122) throw new Win32Exception(error);
        }
        if (size == 0) return null;
        var text = new StringBuilder(checked((int)size));
        if (!GetDefaultPrinter(text, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }

    public void RestartSpooler()
    {
        using var service = new ServiceController("Spooler");
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "AddPrinterConnectionW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddPrinterConnection(string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "DeletePrinterConnectionW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeletePrinterConnection(string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultPrinter(string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(StringBuilder? name, ref uint size);
}
