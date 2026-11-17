namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds settings for connecting storage locations and checking their availability.
/// </summary>
public sealed class StorageMountOptions
{
    public string RootPath { get; set; } = ServerDataPaths.MountsRelative;
    public int CheckIntervalSeconds { get; set; } = 300;
}
