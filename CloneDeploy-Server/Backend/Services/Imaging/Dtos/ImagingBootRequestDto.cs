using System.ComponentModel.DataAnnotations;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the device details and choices supplied when requesting an imaging boot environment.
/// </summary>
public sealed class ImagingBootRequestDto
{
    [StringLength(16)] public string? Arch { get; set; }
    [StringLength(16)] public string? Platform { get; set; }
    [StringLength(500)] public string Manufacturer { get; set; } = "";
    [StringLength(500)] public string Model { get; set; } = "";
    [StringLength(500)] public string? Serial { get; set; }
    [StringLength(100)] public string? Uuid { get; set; }
}
