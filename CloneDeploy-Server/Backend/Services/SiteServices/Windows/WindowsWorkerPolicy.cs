namespace CloneDeploy_Server.Backend.Services;

public class WindowsWorkerPolicy
{
    public virtual bool RemoteAllowed => OperatingSystem.IsLinux();
    public void RequireRemote()
    {
        if (!RemoteAllowed) throw new InvalidDataException("Remote Windows Workers are available only when main runs on Linux. Windows main uses local processing.");
    }
}
