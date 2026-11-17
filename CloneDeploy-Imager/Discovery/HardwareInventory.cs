using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CloneDeploy_Imager.Discovery;

/// <summary>Partition styles reported by the Windows MSFT_Disk provider.</summary>
internal enum DiskPartitionStyle : uint { Raw = 0, Mbr = 1, Gpt = 2 }

/// <summary>
/// Describes a discovered physical disk and its partition style, capacity, and access state.
/// </summary>
internal sealed record DiskInfo(uint Number, string Name, string Serial, ulong Size, uint PartitionStyle, bool Offline, bool ReadOnly);
/// <summary>
/// Identifies a partition containing Windows and whether it hosts the currently running system.
/// </summary>
internal sealed record WindowsVolume(uint DiskNumber, uint PartitionNumber, string Root, bool RunningWindows);
/// <summary>
/// Contains a network adapter name, MAC address, and discovered IP addresses.
/// </summary>
internal sealed record NetworkInfo(string Name, string MacAddress, string[] Addresses)
{
    public string[] IpNetworks { get; init; } = [];
}
/// <summary>
/// Collects machine identity, environment, disks, network adapters, and Windows discovery results.
/// </summary>
internal sealed record HardwareInventory(string Manufacturer, string Model, string Serial, string Uuid, bool IsWinPe,
    IReadOnlyList<DiskInfo> Disks, IReadOnlyList<WindowsVolume> WindowsVolumes, IReadOnlyList<NetworkInfo> Networks,
    IReadOnlyList<string> Notices);

/// <summary>
/// Builds hardware inventory and locates accessible Windows installations using Windows management data.
/// </summary>
internal sealed class HardwareDiscovery
{
    public Task<HardwareInventory> ReadAsync(CancellationToken ct) => Task.Run(() => Read(ct), ct);

    private static HardwareInventory Read(CancellationToken ct)
    {
        var machine = Wmi.Query(@"root\cimv2", "Win32_ComputerSystem", ["Manufacturer", "Model"], ct).FirstOrDefault();
        var product = Wmi.Query(@"root\cimv2", "Win32_ComputerSystemProduct", ["IdentifyingNumber", "UUID"], ct).FirstOrDefault();
        var disks = Wmi.Query(@"root\Microsoft\Windows\Storage", "MSFT_Disk",
            ["Number", "FriendlyName", "SerialNumber", "Size", "PartitionStyle", "IsOffline", "IsReadOnly"], ct)
            .Select(x => new DiskInfo(Convert.ToUInt32(x["Number"]), Text(x, "FriendlyName"), Text(x, "SerialNumber"),
                Convert.ToUInt64(x["Size"]), Convert.ToUInt32(x["PartitionStyle"]),
                Convert.ToBoolean(x["IsOffline"]), Convert.ToBoolean(x["IsReadOnly"]))).OrderBy(x => x.Number).ToArray();
        var partitions = Wmi.Query(@"root\Microsoft\Windows\Storage", "MSFT_Partition",
            ["DiskNumber", "PartitionNumber", "AccessPaths"], ct);
        var windows = new List<WindowsVolume>();
        var notices = new List<string>();
        foreach (var partition in partitions)
        {
            ct.ThrowIfCancellationRequested();
            var disk = Convert.ToUInt32(partition["DiskNumber"]);
            var number = Convert.ToUInt32(partition["PartitionNumber"]);
            var paths = (partition["AccessPaths"] as Array)?.Cast<object>().Select(Convert.ToString)
                .Where(x => !string.IsNullOrEmpty(x)).Cast<string>().OrderBy(x => x.Length).ToArray() ?? [];
            if (paths.Length == 0)
            {
                notices.Add($"Disk {disk}, partition {number}: no accessible volume path; not inspected.");
                continue;
            }
            // Check every alias, including the drive letter, before choosing one display path.
            var running = paths.Any(path => path.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
            var inspected = false;
            foreach (var root in paths)
            {
                try
                {
                    _ = File.GetAttributes(root); // Do not misreport an inaccessible/locked volume as empty.
                    var windowsRoot = Path.Combine(root, "Windows");
                    if (HasFile(Path.Combine(windowsRoot, "System32", "ntoskrnl.exe")) &&
                        HasFile(Path.Combine(windowsRoot, "System32", "config", "SYSTEM")))
                    {
                        windows.Add(new(disk, number, root, running));
                        inspected = true;
                        break;
                    }
                    inspected = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (!inspected) notices.Add($"Disk {disk}, partition {number}: inaccessible or locked; not inspected.");
        }
        using var peKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        var networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(x => new NetworkInfo(x.Name, x.GetPhysicalAddress().ToString(),
                x.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()).ToArray())
            { IpNetworks = x.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}/{a.PrefixLength}").ToArray() })
            .Where(x => x.Addresses.Length > 0).ToArray();
        return new(Text(machine, "Manufacturer"), Text(machine, "Model"), Text(product, "IdentifyingNumber"),
            Text(product, "UUID"), peKey is not null, disks, windows, networks, notices);
    }

    private static string Text(Dictionary<string, object?>? row, string name) =>
        row is not null && row.TryGetValue(name, out var value) ? Convert.ToString(value)?.Trim() ?? "" : "";

    private static bool HasFile(string path)
    {
        try { return !File.GetAttributes(path).HasFlag(FileAttributes.Directory); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

// Uses Windows' COM WMI interface; no PowerShell process or System.Management package.
/// <summary>
/// Wraps COM-based WMI queries and releases management objects after reading their properties.
/// </summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>> Query(string scope, string className, string[] properties, CancellationToken ct)
    {
        object? locator = null, services = null, rows = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!);
            services = ((dynamic)locator!).ConnectServer(".", scope);
            rows = ((dynamic)services).ExecQuery($"SELECT {string.Join(",", properties)} FROM {className}", "WQL", 0);
            var result = new List<Dictionary<string, object?>>();
            // ItemIndex avoids IDispatch enumeration differences across .NET/WinPE versions.
            int count = ((dynamic)rows).Count;
            for (var index = 0; index < count; index++)
            {
                object row = ((dynamic)rows).ItemIndex(index);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var values = new Dictionary<string, object?>();
                    object? propertySet = null;
                    try
                    {
                        propertySet = ((dynamic)row).Properties_;
                        foreach (var name in properties)
                        {
                            object? property = null;
                            try { property = ((dynamic)propertySet).Item(name); values[name] = ((dynamic)property).Value; }
                            finally { Release(property); }
                        }
                    }
                    finally { Release(propertySet); }
                    result.Add(values);
                }
                finally { Release(row); }
            }
            return result;
        }
        finally { Release(rows); Release(services); Release(locator); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
