namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the manufacturer and model rules used to choose a device's boot environment.
/// </summary>
public sealed class WinPeModelRuleDto
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? ManufacturerRegex { get; set; }
    public string? ModelRegex { get; set; }
}
