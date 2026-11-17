namespace CloneDeploy_Server.Backend.Enum;

public enum ImagingStatus
{
    TaskCreated = 0,
    WaitingForLogin = 1,
    CheckedIn = 2,
    InImagingQueue = 3,
    Imaging = 4,
    Succeeded = 5,
    Failed = 6,
    Cancelled = 7,
    Interrupted = 8
}
