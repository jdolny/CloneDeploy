using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Signs users into the imaging environment with password and additional verification checks.
/// </summary>
public sealed class ImagingLoginService(UserManager<AppUser> users, SignInManager<AppUser> signIn,
    ImagingAccess access, ImagingTokens tokens, ILogger<ImagingLoginService> logger, AdLoginService? adLogin = null, SecurityActivity? security = null)
{
    public async Task<ServiceResult<ImagingSessionTokenDto>> LoginAsync(ImagingLoginRequestDto request)
    {
        return await ServiceOperations.RunAsync(async () =>
        {
            var result = await LoginCoreAsync(request);
            if (security is not null && (result.Success || result.Error?.Code is ServiceErrorCodes.Unauthorized or ServiceErrorCodes.Forbidden))
            {
                try
                {
                    var user = await users.FindByNameAsync(request.UserName);
                    var locked = user is not null && await users.IsLockedOutAsync(user);
                    await security.SignInAsync(request.UserName, "Imaging password/MFA", result.Success ? SecurityActivity.Success :
                        locked ? SecurityActivity.Lockout : SecurityActivity.Failure);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not record imaging authentication activity"); }
            }
            return result;
        }, logger);
    }

    private async Task<ServiceResult<ImagingSessionTokenDto>> LoginCoreAsync(ImagingLoginRequestDto request)
    {
        var ad = adLogin is null ? null : await adLogin.AuthenticateAsync(request.UserName, request.Password);
        var user = ad?.Handled == true ? ad.User : await users.FindByNameAsync(request.UserName);
        if (user is null) return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Unauthorized);
        if (ad?.Handled != true && (await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "ActiveDirectory"))
            return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Unauthorized);
        // Keep lockout counters until the complete password + optional imaging MFA login succeeds.
        if (!await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user)) return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Unauthorized);
        if (ad?.Handled != true && !await users.CheckPasswordAsync(user, request.Password))
        { await users.AccessFailedAsync(user); return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Unauthorized); }
        var verified = false;
        if (await access.RequiresMfaAsync(user))
        {
            if (!user.TwoFactorEnabled) return ServiceResult<ImagingSessionTokenDto>.Fail("Imaging.MfaEnrollmentRequired");
            if (string.IsNullOrWhiteSpace(request.AuthenticatorCode)) return ServiceResult<ImagingSessionTokenDto>.Fail("Imaging.MfaRequired");
            verified = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider,
                request.AuthenticatorCode.Replace(" ", "").Replace("-", ""));
            if (!verified) { await users.AccessFailedAsync(user); return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Unauthorized); }
        }
        if (!await access.Allowed(user)) return ServiceResult<ImagingSessionTokenDto>.Fail(ServiceErrorCodes.Forbidden);
        await users.ResetAccessFailedCountAsync(user);
        return ServiceResult<ImagingSessionTokenDto>.Ok(tokens.Issue(user, verified));
    }
}
