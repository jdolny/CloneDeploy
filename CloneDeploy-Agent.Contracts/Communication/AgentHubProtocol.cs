namespace CloneDeploy_Agent.Contracts;

public static class AgentHubProtocol
{
    public const string Wake = "Wake";
    public const string Route = "/api/agent/v1/live";
    public const string ImagingBoot = "ImagingBoot";
    public const string ReadLog = "ReadLog";
    public const string DeviceTools = "DeviceTools";
    public const string RemoteDesktop = "RemoteDesktop";
    public const string RemoteDesktopStop = "RemoteDesktopStop";
    public const string Terminal = "Terminal";
    public const string DesktopSessions = "DesktopSessions";
    public const string CheckInNow = "CheckInNow";
    public const string TasksAvailable = "TasksAvailable";
    public const string HelpPresence = "HelpPresence";
    public const string HelpChanged = "HelpChanged";
    public const string Heartbeat = "Heartbeat";
    public const string ReportActivity = "ReportActivity";
    public const string CollectInventory = "CollectInventory";
}
