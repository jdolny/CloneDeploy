namespace CloneDeploy_Server.Backend.Enum;

public enum StoredFileStatus
{

    Downloading = 0,
    CalculatingSha256 = 1,
    CalculatingMd5 = 2,
    Complete = 3,
    Error = 4,
    Queued = 5

}