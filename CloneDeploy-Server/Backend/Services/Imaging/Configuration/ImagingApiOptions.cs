namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds imaging access settings.
/// </summary>
public sealed class ImagingApiOptions
{
    public int SessionMinutes { get; set; } = 60;
    public string AutomationTokenSha256 { get; set; } = "";
}
