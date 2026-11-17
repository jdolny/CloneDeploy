using Microsoft.Win32;

namespace CloneDeploy.SiteServices;

public static class ServiceHostBoundary
{
    private static Mutex? hostLock;
    public static void AssertSiteService() => AssertOtherAbsent("CloneDeploy-WindowsWorker");
    public static void AssertWindowsWorker()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Worker requires Windows.");
        AssertOtherAbsent("CloneDeploy-SiteService");
    }
    private static void AssertOtherAbsent(string other)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + other);
        using var installation = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CloneDeploy\Installation\" + (other.EndsWith("WindowsWorker") ? "WindowsWorker" : "SiteService"));
        if (service is not null || installation is not null)
            throw new InvalidOperationException("Site Service and Windows Worker cannot share a host. Uninstall the other component first.");
        // Also reject manually launched copies that bypass Windows service registration.
        hostLock = new Mutex(true, @"Global\CloneDeploy-SiteOrWorker", out var created);
        if (!created) throw new InvalidOperationException("A Site Service or Windows Worker instance is already running on this host.");
    }
}
