namespace CloneDeploy_Server.Backend.Services;

/// <summary>Application-owned directories beneath the server data root, never provider content.</summary>
public sealed class ServerDataPaths(IHostEnvironment environment)
{
    public const string MountsRelative = "Mounts";
    public const string UploadsRelative = "Published/Files";
    public const string UploadStagingRelative = "Staging/Uploads";
    public const string WinPeRelative = "Staging/WinPe";

    private string Resolve(string relative) => Path.GetFullPath(relative, environment.ContentRootPath);
    public string Published => Resolve("Published");
    public string Tftp => Resolve("Published/Tftp");
    public string AgentInstallers => Resolve("Published/AgentInstallers");
    public string Mounts => Resolve(MountsRelative);
    public string Staging => Resolve("Staging");
    public string WinPe => Resolve(WinPeRelative);
    public string PxePreparation => Resolve("Staging/PxeBootFiles");
    public string NativePxeStaging => Resolve("Staging/NativePxe");
    public string WindowsWorkerJobs => Resolve("Staging/WindowsWorkerJobs");
    public string Temp => Resolve("Temp");

    public string EnsureTempDirectory()
    {
        CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(Temp);
        Directory.CreateDirectory(Temp);
        return Temp;
    }
}
