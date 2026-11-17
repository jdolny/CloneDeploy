namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Marks a Windows capture/input failure that can occur while the active desktop is switching.</summary>
internal sealed class DesktopUnavailableException(string message) : IOException(message);
