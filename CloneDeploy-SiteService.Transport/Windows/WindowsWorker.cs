using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace CloneDeploy.SiteServices;

[Flags]
public enum WindowsWorkerRoles { None = 0, WinPe = 1, Drivers = 2, AgentPackages = 4 }
public sealed record WindowsWorkFile(string Name, long Length, string Sha256);
public sealed record WindowsWorkOffer(Guid Id, Guid Lease, string Kind, string Request, WindowsWorkFile[] Inputs);
public sealed record WindowsWorkCompletion(string? Error, string? Result, WindowsWorkFile[] Outputs);
public sealed record WindowsWorkProgress(string Message);
public sealed record WindowsWorkerHealth(string? BuildHealth, string? SetupHealth);
public sealed record DriverWorkRequest(int? Vendor, string? CatalogUrl);