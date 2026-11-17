namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Holds the account details and preferences submitted when signing in.
/// </summary>
public sealed class LoginRequestDto
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
