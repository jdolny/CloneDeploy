using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DriverCatalogPackageDto(DriverVendor Vendor, string Model, string OperatingSystem, string Architecture,
    string Release, DateTime? ReleasedAtUtc, string FileName, string Url, string? Sha256, string Source)
{
    public long? DownloadSizeBytes { get; init; }
    public string Key => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Vendor}|{Model}|{OperatingSystem}|{Architecture}|{Url}")));
}

