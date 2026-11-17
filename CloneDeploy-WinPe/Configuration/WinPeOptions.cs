namespace CloneDeploy_Server.WinPe;

/// <summary>
/// Holds folder locations, tool paths, and time limits for building Windows boot images.
/// </summary>
public sealed class WinPeOptions
{
    public string RootPath { get; set; } = "App_Data/WinPe";
    // Empty selects a protected machine-wide directory, outside the server-writable data tree.
    public string PrivateRootPath { get; set; } = "";
    // Explicitly trusted queue producer. The MSI supplies the server service account SID.
    public string QueueWriterSid { get; set; } = "";
    public string AdkPath { get; set; } = @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit";
    public int TimeoutMinutes { get; set; } = 120;
}
