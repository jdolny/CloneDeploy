using System.ComponentModel.DataAnnotations;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the credentials submitted when signing into the imaging environment.
/// </summary>
public sealed class ImagingLoginRequestDto
{
    [Required, StringLength(256)] public string UserName { get; set; } = "";
    [Required, StringLength(1024)] public string Password { get; set; } = "";
    [StringLength(32)] public string? AuthenticatorCode { get; set; }
}
