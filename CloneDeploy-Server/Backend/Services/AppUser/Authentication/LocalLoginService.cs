using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Handles password sign-in and additional verification for local user accounts.
/// </summary>
public sealed class LocalLoginService(
    SignInManager<AppUser> signIn,
    UserManager<AppUser> users,
    ILogger<LocalLoginService> logger,
    ICloneDeployDbFactory factory,
    AdLoginService? adLogin = null, SecurityActivity? security = null)
{
    private async Task<bool> RequiresWebAsync(AppUser user)
    {
        await using var db = await factory.CreateContextAsync();
        return await MfaPolicy.RequiresWebAsync(db, user);
    }

    public async Task<LocalLoginResultDto> LoginAsync(LoginRequestDto request)
    {
        var outcome = await ExecuteAsync(async () =>
    {
        if (adLogin is not null)
        {
            var ad = await adLogin.AuthenticateAsync(request.UserName, request.Password);
            if (ad.Handled)
            {
                if (ad.User is null) return new(IsLockedOut: ad.IsLockedOut);
                var adResult = await signIn.ExternalLoginSignInAsync("ActiveDirectory", ad.Key!, isPersistent: false, bypassTwoFactor: false);
                if (adResult.Succeeded) await users.ResetAccessFailedCountAsync(ad.User);
                return new(Succeeded: adResult.Succeeded, RequiresTwoFactor: adResult.RequiresTwoFactor, IsLockedOut: adResult.IsLockedOut,
                    RequiresEnrollment: adResult.Succeeded && await RequiresWebAsync(ad.User) && !ad.User.TwoFactorEnabled);
            }
        }
        var passwordUser = await users.FindByNameAsync(request.UserName);
        if (passwordUser is not null && (await users.GetLoginsAsync(passwordUser))
            .Any(x => x.LoginProvider is "Entra" or "ActiveDirectory")) return new();
        var result = await signIn.PasswordSignInAsync(request.UserName, request.Password,
            isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
            return new(RequiresTwoFactor: result.RequiresTwoFactor, IsLockedOut: result.IsLockedOut);

        var user = await users.FindByNameAsync(request.UserName);
        if (user is null)
        {
            await signIn.SignOutAsync();
            return new();
        }
        return new(Succeeded: true, RequiresEnrollment: await RequiresWebAsync(user) && !user.TwoFactorEnabled);
        });
        await RecordAsync(request.UserName, "Web password (local/AD)", outcome);
        return outcome;
    }

    public Task<LocalLoginResultDto> MfaAsync(MfaLoginRequestDto request) => ExecuteAsync(async () =>
    {
        var user = security is null ? null : await signIn.GetTwoFactorAuthenticationUserAsync();
        var code = request.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false,
            rememberClient: false);
        var outcome = new LocalLoginResultDto(Succeeded: result.Succeeded, IsLockedOut: result.IsLockedOut);
        await RecordAsync(user?.UserName ?? "Unknown MFA session", "Web MFA", outcome);
        return outcome;
    });

    private Task RecordAsync(string name, string channel, LocalLoginResultDto result)
    {
        if (security is null || result.Failed || result.RequiresTwoFactor || result.RequiresEnrollment) return Task.CompletedTask;
        return security.SignInAsync(name, channel, result.IsLockedOut ? SecurityActivity.Lockout :
            result.Succeeded ? SecurityActivity.Success : SecurityActivity.Failure);
    }

    private async Task<LocalLoginResultDto> ExecuteAsync(Func<Task<LocalLoginResultDto>> operation,
        [CallerMemberName] string operationName = "")
    {
        try { return await operation(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Web authentication operation {Operation} failed", operationName);
            // Sign-in may already have issued a cookie. Clear it before reporting failure.
            await signIn.SignOutAsync();
            return new(Failed: true);
        }
    }
}
