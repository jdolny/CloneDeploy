namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the verification code and preferences submitted during additional sign-in verification.
/// </summary>
public sealed class MfaLoginRequestDto
{
    public string Code { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
