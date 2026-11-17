using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Creates and checks the access passes used by imaging sessions and automation.
/// </summary>
public sealed class ImagingTokens(IDataProtectionProvider protection, IOptionsMonitor<ImagingApiOptions> options)
{
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("CloneDeploy.Imaging.UserSession.v1").ToTimeLimitedDataProtector();
    public ImagingSessionTokenDto Issue(AppUser user, bool mfaVerified)
        => IssuePayload(new ImagingTokenPayloadDto(user.Id.ToString(), user.SecurityStamp ?? "", mfaVerified));

    // Only called after ImagingBearer authentication and active-upload ownership checks.
    // Preserve the original security stamp and MFA proof; never upgrade either on renewal.
    public ImagingSessionTokenDto RenewCapture(string token)
        => IssuePayload(Read(token) ?? throw new InvalidOperationException("The imaging session has expired. Sign in again."));

    private ImagingSessionTokenDto IssuePayload(ImagingTokenPayloadDto payload)
    {
        var seconds = Math.Clamp(options.CurrentValue.SessionMinutes, 5, 480) * 60;
        return new("Bearer", "cdi_user_" + _protector.Protect(JsonSerializer.Serialize(payload), TimeSpan.FromSeconds(seconds)), seconds);
    }
    public ImagingTokenPayloadDto? Read(string token)
    {
        if (!token.StartsWith("cdi_user_", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<ImagingTokenPayloadDto>(_protector.Unprotect(token[9..])); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
    public bool IsAutomation(string token)
    {
        if (!token.StartsWith("cdi_auto_", StringComparison.Ordinal) || token.Length < 52) return false;
        var configured = options.CurrentValue.AutomationTokenSha256;
        if (configured.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(configured)); }
        catch (FormatException) { return false; }
    }
}
