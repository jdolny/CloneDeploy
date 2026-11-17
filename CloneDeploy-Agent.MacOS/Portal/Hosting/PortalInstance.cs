namespace CloneDeploy_Agent.MacOS.Portal;

// A second Finder launch asks the existing user process to show its window.
// These files belong to the user and never carry requests to the root daemon.
internal sealed class PortalInstance : IDisposable
{
    private readonly FileStream instanceLock;
    private readonly FileSystemWatcher watcher;
    public event Action? OpenRequested;
    private PortalInstance(FileStream instanceLock, string directory)
    {
        this.instanceLock = instanceLock;
        watcher = new FileSystemWatcher(directory, "open") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        watcher.Changed += (_, _) => OpenRequested?.Invoke();
        watcher.Created += (_, _) => OpenRequested?.Invoke();
        watcher.EnableRaisingEvents = true;
    }
    public static PortalInstance? Acquire()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "com.clonedeploy.agent.portal");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { return new(new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), directory); }
        catch (IOException)
        {
            // Login/launchd retries must not turn a background launch into an open request.
            if (!Environment.GetCommandLineArgs().Contains("--background"))
                File.WriteAllText(Path.Combine(directory, "open"), Guid.NewGuid().ToString("N"));
            return null;
        }
    }
    public void Dispose() { watcher.Dispose(); instanceLock.Dispose(); }
}
