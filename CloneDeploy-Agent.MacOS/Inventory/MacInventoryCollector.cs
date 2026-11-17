using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Read-only native probes; optional sections remain null when unavailable.</summary>
public sealed class MacInventoryCollector
{
    private const int MaximumProbeCharacters = 8 * 1024 * 1024;
    private const long BytesPerGiB = 1024L * 1024 * 1024;
    private const int BytesPerMiB = 1024 * 1024;
    private const int MaximumInventoryItems = 256;
    private const int MaximumApplicationFieldCharacters = 1000;
    private const int MaximumApplications = 10000;
    private const int ProbeBufferCharacters = 8192;

    private readonly Func<string, string[], CancellationToken, Task<string>> command;
    public MacInventoryCollector(Func<string, string[], CancellationToken, Task<string>>? command = null) => this.command = command ?? RunCommandAsync;
    public async Task<AgentInventory> CollectAsync(CancellationToken ct)
    {
        var inventory = new AgentInventory();
        async Task Section(string name, Func<Task> collect)
        {
            try { await collect(); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { inventory.Warnings.Add(name); Console.Error.WriteLine($"Inventory {name} unavailable ({ex.GetType().Name})."); }
        }
        async Task<string> Sysctl(string key) => (await command("/usr/sbin/sysctl", ["-n", key], ct)).Trim();
        await Section("ComputerSystem", async () =>
        {
            var model = await Sysctl("hw.model");
            var memory = long.Parse(await Sysctl("hw.memsize"), CultureInfo.InvariantCulture);
            inventory.ComputerSystem = new() { Name = Environment.MachineName, Manufacturer = "Apple Inc.", Model = model, Memory = Units(memory, BytesPerMiB) };
        });
        await Section("Os", async () =>
        {
            var version = (await command("/usr/bin/sw_vers", ["-productVersion"], ct)).Trim();
            var build = (await command("/usr/bin/sw_vers", ["-buildVersion"], ct)).Trim();
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(build)) throw new InvalidDataException("Missing OS version.");
            inventory.Os = new() { Caption = "macOS " + version, Version = version, BuildNumber = build, OSArchitecture = "arm64", NativeArchitecture = "arm64", LocalTimeZone = TimeZoneInfo.Local.Id };
        });
        await Section("Bios", async () =>
        {
            var xml = Plist(await command("/usr/sbin/ioreg", ["-rd1", "-c", "IOPlatformExpertDevice", "-a"], ct));
            var hardware = xml.Descendants("dict").First();
            inventory.Bios = new() { SerialNumber = Value(hardware, "IOPlatformSerialNumber")?.Value };
            var uuid = Value(hardware, "IOPlatformUUID")?.Value;
            if (Guid.TryParse(uuid, out var id) && id != Guid.Empty) inventory.HardwareUuid = id.ToString();
        });
        await Section("Processor", async () =>
        {
            inventory.Processor = new() { Name = await Sysctl("machdep.cpu.brand_string"), Cores = int.Parse(await Sysctl("hw.physicalcpu"), CultureInfo.InvariantCulture) };
        });
        await Section("HardDrives", async () =>
        {
            var list = Plist(await command("/usr/sbin/diskutil", ["list", "-plist", "physical"], ct));
            var drives = new List<InventoryHardDrive>();
            var disks = Value(list.Root!.Element("dict")!, "AllDisksAndPartitions")?.Elements("dict")
                ?? throw new InvalidDataException("Missing physical disk list.");
            foreach (var disk in disks.Take(64))
            {
                var id = Value(disk, "DeviceIdentifier")?.Value ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^disk[0-9]+$")) throw new InvalidDataException("Invalid disk identifier.");
                var info = Plist(await command("/usr/sbin/diskutil", ["info", "-plist", id], ct)).Root!.Element("dict")!;
                var size = Value(info, "TotalSize")?.Value ?? Value(info, "DiskSize")?.Value ?? Value(disk, "Size")?.Value;
                drives.Add(new() { Model = Value(info, "MediaName")?.Value ?? Value(info, "DeviceModel")?.Value ?? id,
                    SizeMb = Units(long.Parse(size!, CultureInfo.InvariantCulture), BytesPerMiB), Status = Value(info, "SMARTStatus")?.Value });
            }
            inventory.HardDrives = drives;
        });
        await Section("LogicalVolumes", () =>
        {
            inventory.LogicalVolumes = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => new InventoryLogicalVolume { Drive = d.Name, SizeGB = Units(d.TotalSize, BytesPerGiB), FreeSpaceGB = Units(d.AvailableFreeSpace, BytesPerGiB),
                    FreeSpacePercent = d.TotalSize > 0 ? (int)Math.Clamp(100d * d.AvailableFreeSpace / d.TotalSize, 0, 100) : 0 }).Take(MaximumInventoryItems).ToList();
            return Task.CompletedTask;
        });
        await Section("Nics", () =>
        {
            inventory.Nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback).Take(MaximumInventoryItems).Select(n =>
            {
                var addresses = n.GetIPProperties();
                return new InventoryNic { Name = n.Name, Description = n.Description, Type = n.NetworkInterfaceType.ToString(), Mac = n.GetPhysicalAddress().ToString(), Status = n.OperationalStatus.ToString(),
                    Ips = string.Join(",", addresses.UnicastAddresses.Select(x => x.Address.ToString())),
                    IpNetworks = string.Join(",", addresses.UnicastAddresses.Select(x => $"{x.Address}/{x.PrefixLength}")),
                    Gateways = string.Join(",", addresses.GatewayAddresses.Select(x => x.Address.ToString())) };
            }).ToList();
            return Task.CompletedTask;
        });
        await Section("Software", async () => inventory.Software = ParseApplications(await command("/usr/sbin/system_profiler", ["SPApplicationsDataType", "-json", "-timeout", "60"], ct)));
        return inventory;
    }
    public static List<InventorySoftware> ParseApplications(string json)
    {
        using var document = JsonDocument.Parse(json);
        var apps = document.RootElement.GetProperty("SPApplicationsDataType");
        var result = new List<InventorySoftware>();
        foreach (var app in apps.EnumerateArray())
        {
            var name = app.TryGetProperty("_name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var version = app.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (name.Length > MaximumApplicationFieldCharacters || version?.Length > MaximumApplicationFieldCharacters) continue;
            Version.TryParse(version, out var parsed);
            result.Add(new() { Name = name, Version = version, Major = parsed?.Major ?? 0, Minor = Math.Max(0, parsed?.Minor ?? 0), Build = Math.Max(0, parsed?.Build ?? 0), Revision = Math.Max(0, parsed?.Revision ?? 0) });
            if (result.Count > MaximumApplications) throw new InvalidDataException("Too many installed applications.");
        }
        return result.DistinctBy(x => (x.Name, x.Version)).ToList();
    }
    private static int Units(long bytes, long divisor) => (int)Math.Clamp(bytes / divisor, 0, int.MaxValue);
    private static XElement? Value(XElement dictionary, string key) => dictionary.Elements("key").FirstOrDefault(x => x.Value == key)?.ElementsAfterSelf().FirstOrDefault();
    private static XDocument Plist(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumProbeCharacters });
        return XDocument.Load(reader);
    }
    private static async Task<string> RunCommandAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start inventory probe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(75));
        async Task<string> ReadBounded(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[ProbeBufferCharacters]; int count;
            while ((count = await reader.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (result.Length + count > MaximumProbeCharacters) { timeout.Cancel(); throw new InvalidDataException("Inventory probe output exceeds its size limit."); }
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        var output = ReadBounded(process.StandardOutput); var errors = ReadBounded(process.StandardError);
        try { await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new IOException($"Inventory probe {Path.GetFileName(executable)} exited with {process.ExitCode}.");
        return await output;
    }
}
