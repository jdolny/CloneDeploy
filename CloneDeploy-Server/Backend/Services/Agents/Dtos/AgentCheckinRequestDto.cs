using System.ComponentModel.DataAnnotations;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AgentCheckinRequestDto([Required, StringLength(64)] string ClientVersion,
    [MaxLength(1000)] List<AgentUserLogin>? UserLogins = null)
{
    [StringLength(128)] public string? ComputerName { get; init; }
    public bool SupportsCertificateRenewal { get; init; }
    public bool SupportsMacPackageUpdates { get; init; }
    [StringLength(16384)] public string? CertificateRenewalCsr { get; init; }
    [Required, RegularExpression("^(Windows|macOS)$")] public string Platform { get; init; } = AgentPlatform.Windows;
    [Required, RegularExpression("^(x64|arm64)$")] public string Architecture { get; init; } = "x64";
}
