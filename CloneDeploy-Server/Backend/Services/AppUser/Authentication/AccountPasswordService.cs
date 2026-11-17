using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AccountPasswordOptions(bool IsEntra, bool HasPassword);

// Self-service only: the account is always resolved from the authenticated session.
public sealed class AccountPasswordService(
    UserManager<AppUser> users,
    AuthenticationStateProvider authentication,
    ILogger<AccountPasswordService> logger,
    ICloneDeployDbFactory factory)
{
    private async Task<bool> RequiresWebAsync(AppUser user)
    {
        await using var db = await factory.CreateContextAsync();
        return await MfaPolicy.RequiresWebAsync(db, user);
    }

    private async Task<AppUser?> CurrentAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true) return null;
        var user = await users.GetUserAsync(principal);
        if (user is null || await users.IsLockedOutAsync(user) || (!MfaPolicy.IsEntraSession(principal) && await RequiresWebAsync(user) && !user.TwoFactorEnabled)) return null;
        if ((await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "ActiveDirectory")) return null;
        return user;
    }

    public Task<ServiceResult<AccountPasswordOptions>> GetAsync() => ServiceOperations.RunAsync(async () =>
    {
        var user = await CurrentAsync();
        if (user is null) return ServiceResult<AccountPasswordOptions>.Fail(ServiceErrorCodes.Forbidden);
        return ServiceResult<AccountPasswordOptions>.Ok(new(
            (await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "Entra"), await users.HasPasswordAsync(user)));
    }, logger);

    public Task<ServiceResult> ChangeAsync(string? currentPassword, string newPassword, string confirmation) => ServiceOperations.RunAsync(async () =>
    {
        var user = await CurrentAsync();
        if (user is null) return ServiceResult.Fail(ServiceErrorCodes.Forbidden);
        if (string.IsNullOrEmpty(newPassword) || newPassword != confirmation)
            return ServiceResult.Fail("Common.IdentityError", UiText.Get(LocKeys.Ui.AccountPasswordService.EnterANewPasswordAndMatchingConfirmation));
        IdentityResult result;
        if (await users.HasPasswordAsync(user))
        {
            if (!await users.CheckPasswordAsync(user, currentPassword ?? ""))
            {
                await users.AccessFailedAsync(user);
                return ServiceResult.Fail("Common.IdentityError", UiText.Get(LocKeys.Ui.AccountPasswordService.TheCurrentPasswordIsIncorrect));
            }
            result = await users.ChangePasswordAsync(user, currentPassword!, newPassword);
        }
        else
        {
            if (!(await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "Entra"))
                return ServiceResult.Fail(ServiceErrorCodes.Forbidden);
            result = await users.AddPasswordAsync(user, newPassword);
        }
        if (!result.Succeeded) return ServiceResult.Fail("Common.IdentityError", string.Join(" ", result.Errors.Select(x => x.Description)));
        await users.ResetAccessFailedCountAsync(user);
        return ServiceResult.Ok();
    }, logger);
}
