namespace CloneDeploy_Server.Backend.Services.Files;

/// <summary>
/// Holds upload limits, storage locations, and cleanup settings.
/// </summary>
public sealed class UploadOptions
{
    public string StoragePath { get; set; } = ServerDataPaths.UploadsRelative;
    public string TempStoragePath { get; set; } = ServerDataPaths.UploadStagingRelative;
    public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024;
    public int MaxActiveUploads { get; set; } = 20;
}
