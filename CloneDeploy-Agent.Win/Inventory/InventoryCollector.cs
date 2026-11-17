using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32;

namespace CloneDeploy_Agent;

/// <summary>
/// Collects information about the computer, its hardware, and its installed software.
/// </summary>
public sealed class InventoryCollector(ILogger<InventoryCollector> logger)
{
    private const int ScannerStateShift = 8;
    private const int StateByteMask = 255;
    private const int ScannerOff = 0, ScannerExpired = 1, ScannerOn = 16, ScannerSnoozed = 17;
    private const int DefinitionsCurrent = 0, DefinitionsOutdated = 16;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int MinimumInventoryYear = 1900;

    private const long BytesPerGiB = 1024L * 1024 * 1024;
    private const long BytesPerMiB = 1024L * 1024;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    public AgentInventory Collect(CancellationToken ct)
    {
        var result = new AgentInventory();

        T? Scan<T>(string name, Func<T?> collect) where T : class
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return collect();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, $"Inventory section {name} unavailable");
                result.Warnings.Add(name);

                return null;
            }
        }

        result.ComputerSystem = Scan(
            "Computer system",
            () => Query(
                "Win32_ComputerSystem",
                x => new InventoryComputerSystem
                {
                    Name = Text(x, "Name"),
                    Manufacturer = Text(x, "Manufacturer"),
                    Model = Text(x, "Model"),
                    Domain = Text(x, "Domain"),
                    Workgroup = Text(x, "Workgroup"),
                    Memory = Mb(Number(x, "TotalPhysicalMemory"))
                }).FirstOrDefault());
        result.HardwareUuid = Scan(
            "Hardware UUID",
            () => Query("Win32_ComputerSystemProduct", x => Text(x, "UUID")).FirstOrDefault());
        result.Bios = Scan(
            "BIOS",
            () => Query(
                "Win32_BIOS",
                x => new InventoryBios
                {
                    SerialNumber = Text(x, "SerialNumber"),
                    Version = Text(x, "Version"),
                    SMBIOSBIOSVersion = Text(x, "SMBIOSBIOSVersion")
                }).FirstOrDefault());
        result.Os = Scan(
            "Operating system",
            () => Query(
                "Win32_OperatingSystem",
                x => new InventoryOs
                {
                    Caption = Text(x, "Caption"),
                    Version = Text(x, "Version"),
                    BuildNumber = Text(x, "BuildNumber"),
                    OSArchitecture = Text(x, "OSArchitecture"),
                    NativeArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                    LocalTimeZone = TimeZoneInfo.Local.DisplayName,
                    ReleaseId = RegistryText(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion"),
                    UacStatus = RegistryText(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA"),
                    UpdateServer = RegistryText(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer"),
                    SUStargetGroup = RegistryText(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "TargetGroup")
                }).FirstOrDefault());
        if (result.Os is { } os)
            Scan("Location", () => { WindowsLocationCollector.Collect(os, ct); return os; });
        result.Processor = Scan(
            "Processor",
            () => Query(
                "Win32_Processor",
                x => new InventoryProcessor
                {
                    Name = Text(x, "Name"),
                    Speed = (int)Number(x, "MaxClockSpeed"),
                    Cores = (int)Number(x, "NumberOfCores")
                }).FirstOrDefault());
        result.Gpu = Scan(
            "Graphics",
            () => Query(
                "Win32_VideoController",
                x => new InventoryGpu
                {
                    Name = Text(x, "Name"),
                    Memory = Mb(Number(x, "AdapterRAM"))
                }).FirstOrDefault());
        result.HardDrives = Scan(
            "Hard drives",
            () => Query(
                "Win32_DiskDrive",
                x => new InventoryHardDrive
                {
                    Model = Text(x, "Model"),
                    SerialNumber = Text(x, "SerialNumber"),
                    FirmwareRevision = Text(x, "FirmwareRevision"),
                    SizeMb = Mb(Number(x, "Size")),
                    Status = Text(x, "Status")
                }));
        result.LogicalVolumes = Scan(
            "Volumes",
            () => Query(
                "Win32_LogicalDisk WHERE DriveType = 3",
                x => new InventoryLogicalVolume
                {
                    Drive = Text(x, "DeviceID"),
                    SizeGB = (int)(Number(x, "Size") / BytesPerGiB),
                    FreeSpaceGB = (int)(Number(x, "FreeSpace") / BytesPerGiB),
                    FreeSpacePercent = Number(x, "Size") == 0 ? 0 : (int)(100d * Number(x, "FreeSpace") / Number(x, "Size"))
                }));
        result.Nics = Scan("Network adapters", CollectNics);
        result.Printers = Scan(
            "Printers",
            () => Query(
                "Win32_Printer",
                x => new InventoryPrinter
                {
                    Name = Text(x, "Name"),
                    DriverName = Text(x, "DriverName"),
                    Local = Convert.ToBoolean(x["Local"]),
                    Network = Convert.ToBoolean(x["Network"]),
                    ShareName = Text(x, "ShareName"),
                    SystemName = Text(x, "SystemName")
                }));
        result.AntiVirus = Scan(
            "Antivirus",
            () => Query(
                      "AntiVirusProduct",
                      x =>
                      {
                          var state = (int)Number(x, "productState");

                          return new InventoryAntiVirus
                          {
                              DisplayName = Text(x, "displayName"),
                              ProductState = state,
                              Provider = "Security Center",
                              RealtimeScanner = ((state >> ScannerStateShift) & StateByteMask) switch { ScannerOff => "Off", ScannerExpired => "Expired", ScannerOn => "On", ScannerSnoozed => "Snoozed", _ => "Unknown" },
                              DefinitionStatus = (state & StateByteMask) switch { DefinitionsCurrent => "Up To Date", DefinitionsOutdated => "Out Of Date", _ => "Unknown" }
                          };
                      },
                      @"root\SecurityCenter2").FirstOrDefault()
                  ?? new InventoryAntiVirus
                  {
                      DisplayName = "None detected",
                      RealtimeScanner = "Unknown",
                      DefinitionStatus = "Unknown"
                  });
        result.BitLocker = Scan(
            "BitLocker",
            () => Query(
                      "Win32_EncryptableVolume",
                      x => new InventoryBitLocker
                      {
                          DriveLetter = Text(x, "DriveLetter"),
                          Status = (int)Number(x, "ProtectionStatus")
                      },
                      @"root\CIMV2\Security\MicrosoftVolumeEncryption").FirstOrDefault(x => x.DriveLetter == Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'))
                  ?? new InventoryBitLocker
                  {
                      DriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'),
                      Status = -1
                  });
        result.Firewall = Scan(
            "Firewall",
            () =>
            {
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;

                try
                {
                    return new InventoryFirewall
                    {
                        DomainEnabled = policy.FirewallEnabled[1],
                        PrivateEnabled = policy.FirewallEnabled[2],
                        PublicEnabled = policy.FirewallEnabled[4]
                    };
                }
                finally
                {
                    Marshal.FinalReleaseComObject(policy);
                }
            });
        result.Software = Scan("Software", CollectSoftware);
        result.Certificate = Scan("Certificates", CollectCertificates);
        var updateScan = new InventoryWindowsUpdateScan
        {
            CollectedAtUtc = DateTime.UtcNow,
            IsOnline = false
        };
        result.WindowsUpdateScan = updateScan;
        updateScan.Configuration = Scan(
            "Windows Update configuration",
            () => WindowsUpdateConfigurationCollector.Collect(logger));
        ReadUpdateContext(updateScan);
        result.WindowsUpdate = Scan(
            "Windows updates",
            () =>
            {
                try
                {
                    return CollectUpdates(updateScan);
                }
                catch (Exception ex)
                {
                    updateScan.ErrorCode = ex.HResult;
                    throw;
                }
                finally
                {
                    updateScan.CollectedAtUtc = DateTime.UtcNow;
                }
            });

        return result;
    }

    private static List<T> Query<T>(
        string query,
        Func<ManagementBaseObject, T> map,
        string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, $"SELECT * FROM {query}");
        searcher.Options.Timeout = QueryTimeout;
        using var rows = searcher.Get();
        var results = new List<T>();

        foreach (ManagementBaseObject row in rows)
            using (row)
                results.Add(map(row));

        return results;
    }

    private static string Text(ManagementBaseObject row, string property) => Convert.ToString(
                                row[property],
                                CultureInfo.InvariantCulture)?.Trim()
                            ?? "";

    private static long Number(ManagementBaseObject row, string property) => Convert.ToInt64(
        row[property],
        CultureInfo.InvariantCulture);

    private static int Mb(long bytes) => (int)Math.Min(int.MaxValue, bytes / BytesPerMiB);

    private static string? RegistryText(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);

        return Convert.ToString(key?.GetValue(name), CultureInfo.InvariantCulture);
    }

    private static List<InventoryNic> CollectNics()
    {
        var networks = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .GroupBy(x => WakeNetwork.Mac(x.GetPhysicalAddress().ToString()) ?? "")
            .ToDictionary(x => x.Key, x => string.Join(", ", x.SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}/{a.PrefixLength}").Distinct()));
        var configurations = Query(
            "Win32_NetworkAdapterConfiguration WHERE IPEnabled = True",
            x => new
            {
                Index = Number(x, "Index"),
                Ips = string.Join(", ", x["IPAddress"] as string[] ?? []),
                Gateways = string.Join(", ", x["DefaultIPGateway"] as string[] ?? [])
            }).ToDictionary(x => x.Index);

        return Query(
            "Win32_NetworkAdapter WHERE MACAddress IS NOT NULL",
            x =>
            {
                configurations.TryGetValue(Number(x, "Index"), out var config);

                return new InventoryNic
                {
                    Name = Text(x, "NetConnectionID"),
                    Description = Text(x, "Description"),
                    Type = Text(x, "AdapterType"),
                    Mac = Text(x, "MACAddress"),
                    Speed = Number(x, "Speed"),
                    Status = Text(x, "Status"),
                    Ips = config?.Ips,
                    IpNetworks = networks.GetValueOrDefault(WakeNetwork.Mac(Text(x, "MACAddress")) ?? ""),
                    Gateways = config?.Gateways
                };
            });
    }

    private static List<InventorySoftware> CollectSoftware()
    {
        var items = new List<InventorySoftware>();

        void Read(RegistryKey root, string path)
        {
            using var uninstall = root.OpenSubKey(path);

            if (uninstall is null)
                return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);

                if (key?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
                    continue;

                var version = Convert.ToString(key.GetValue("DisplayVersion")) ?? "";
                var parts = version.Split('.').Select(x => int.TryParse(x, out var v) ? v : 0).ToArray();
                items.Add(new InventorySoftware
                {
                    Name = display,
                    Version = version,
                    UninstallString = Convert.ToString(key.GetValue("UninstallString")),
                    Major = parts.ElementAtOrDefault(0),
                    Minor = parts.ElementAtOrDefault(1),
                    Build = parts.ElementAtOrDefault(2),
                    Revision = parts.ElementAtOrDefault(3)
                });
            }
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            Read(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, view);

            foreach (var sid in users.GetSubKeyNames().Where(x => x.StartsWith(
                                                                      "S-1-5-21-",
                                                                      StringComparison.Ordinal)
                                                                  && !x.EndsWith("_Classes")))
                Read(users, $"{sid}\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
        }

        return items.DistinctBy(x => (x.Name, x.Version, x.UninstallString)).ToList();
    }

    private static List<InventoryCertificate> CollectCertificates()
    {
        var items = new List<InventoryCertificate>();

        foreach (var name in Enum.GetValues<StoreName>())
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);

            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException ex) when (ex.HResult == FileNotFoundHResult)
            {
                continue;
            }

            foreach (var certificate in store.Certificates)
                using (certificate)
                    items.Add(new InventoryCertificate
                    {
                        Store = name.ToString(),
                        Subject = certificate.Subject,
                        FriendlyName = certificate.FriendlyName,
                        Thumbprint = certificate.Thumbprint,
                        Serial = certificate.SerialNumber,
                        Issuer = certificate.Issuer,
                        NotBeforeUtc = certificate.NotBefore.ToUniversalTime(),
                        NotAfterUtc = certificate.NotAfter.ToUniversalTime()
                    });
        }

        return items;
    }

    private List<InventoryWindowsUpdate> CollectUpdates(InventoryWindowsUpdateScan scan)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true)!)!;
        dynamic searcher = session.CreateUpdateSearcher();

        try
        {
            // Read the local update catalog; inventory must not initiate downloads or an online scan.
            searcher.Online = false;
            searcher.ServerSelection = 0; // ssDefault, with Online=false uses the local cache.
            dynamic result = searcher.Search("IsInstalled=1 or IsInstalled=0");

            try
            {
                scan.ResultCode = (int)result.ResultCode;

                if (scan.ResultCode != 2)
                    throw new InvalidOperationException("Windows Update inventory search did not complete successfully.");

                dynamic updates = result.Updates;

                try
                {
                    var items = new List<InventoryWindowsUpdate>();

                    for (var i = 0; i < (int)updates.Count; i++)
                    {
                        dynamic update = updates.Item(i);
                        dynamic identity = update.Identity;
                        dynamic categories = update.Categories;
                        dynamic kbIds = update.KBArticleIDs;

                        try
                        {
                            var metadata = new List<InventoryUpdateCategory>();

                            for (var categoryIndex = 0; categoryIndex < (int)categories.Count; categoryIndex++)
                            {
                                dynamic category = categories.Item(categoryIndex);

                                try
                                {
                                    metadata.Add(new()
                                    {
                                        Id = category.CategoryID,
                                        Name = category.Name,
                                        Type = category.Type
                                    });
                                }
                                finally
                                {
                                    Marshal.FinalReleaseComObject(category);
                                }
                            }

                            var articles = new List<string>();

                            for (var kbIndex = 0; kbIndex < (int)kbIds.Count; kbIndex++)
                                articles.Add((string)kbIds.Item(kbIndex));

                            var item = new InventoryWindowsUpdate
                            {
                                Title = update.Title,
                                UpdateId = identity.UpdateID,
                                IsInstalled = update.IsInstalled,
                                RevisionNumber = (int)identity.RevisionNumber,
                                KBArticleIds = articles.Distinct().Order().ToArray(),
                                Categories = metadata.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
                                Category = string.Join(", ", metadata.Select(x => x.Name)),
                                MsrcSeverity = update.MsrcSeverity,
                                Type = (int)update.Type == (int)ActionType.Software ? "Software" : "Driver",
                                IsDownloaded = update.IsDownloaded,
                                IsHidden = update.IsHidden,
                                LastDeploymentChangedAtUtc = DateTime.SpecifyKind(
                                    (DateTime)update.LastDeploymentChangeTime,
                                    DateTimeKind.Utc)
                            };
                            TryReadUpdateDetail(() => item.BrowseOnly = (bool)update.BrowseOnly);
                            items.Add(item);
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(kbIds);
                            Marshal.FinalReleaseComObject(categories);
                            Marshal.FinalReleaseComObject(identity);
                            Marshal.FinalReleaseComObject(update);
                        }
                    }

                    scan.Succeeded = true;

                    return items;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(updates);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(searcher);
            Marshal.FinalReleaseComObject(session);
        }
    }

    private void ReadUpdateContext(InventoryWindowsUpdateScan scan)
    {
        TryReadUpdateDetail(() =>
        {
            dynamic automatic = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", true)!)!;

            try
            {
                dynamic results = automatic.Results;

                try
                {
                    object value = results.LastSearchSuccessDate;

                    if (value is DateTime date && date.Year > MinimumInventoryYear)
                        scan.LastSuccessfulSearchAtUtc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(results);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(automatic);
            }
        });
        TryReadUpdateDetail(() =>
        {
            dynamic system = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true)!)!;

            try
            {
                scan.RebootRequired = (bool)system.RebootRequired;
            }
            finally
            {
                Marshal.FinalReleaseComObject(system);
            }
        });
        TryReadUpdateDetail(() =>
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromProgID(
                "Microsoft.Update.ServiceManager",
                true)!)!;

            try
            {
                dynamic services = manager.Services;

                try
                {
                    for (var i = 0; i < (int)services.Count; i++)
                    {
                        dynamic service = services.Item(i);

                        try
                        {
                            if (!(bool)service.IsDefaultAUService)
                                continue;

                            scan.SourceId = service.ServiceID;
                            scan.SourceName = service.Name;
                            break;
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(service);
                        }
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(services);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(manager);
            }
        });
    }

    private void TryReadUpdateDetail(Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Optional WUA properties may be unavailable. Unknown must remain null, not false.
            logger.LogDebug(ex, "Optional Windows Update inventory detail unavailable");
        }
    }
}
