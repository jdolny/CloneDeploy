namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds size, file count, and time limits for unpacking driver packages.
/// </summary>
public sealed class DriverPreparationOptions
{
    public long MaxExpandedBytes { get; set; } = 40L * 1024 * 1024 * 1024;
    public int MaxFiles { get; set; } = 100000;
    public string? SevenZipPath { get; set; }
    public int TimeoutMinutes { get; set; } = 30;
}

