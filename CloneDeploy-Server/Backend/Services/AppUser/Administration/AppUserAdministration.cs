using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages account details, passwords, and preferences while protecting administrator access.
/// </summary>
public class AppUserAdministration(UserManager<AppUser> userManager, ICloneDeployDbFactory dbFactory)
{
    public async Task<List<AppUser>> GetAsync(string? searchText = null)
    {
        var query = userManager.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            searchText = searchText.Trim();
            query = query.Where(x =>
                (x.UserName != null && EF.Functions.ILike(x.UserName, $"%{searchText}%")) ||
                (x.Email != null && EF.Functions.ILike(x.Email, $"%{searchText}%")));
        }

        var result = await query.OrderBy(x => x.UserName).ToListAsync();
        await using var db = await dbFactory.CreateContextAsync();
        var ids = result.Select(x => x.Id).ToArray();
        var entraIds = (await db.UserLogins.Where(x => x.LoginProvider == "Entra" && ids.Contains(x.UserId))
            .Select(x => x.UserId).ToListAsync()).ToHashSet();
        foreach (var user in result) user.IsEntraManaged = entraIds.Contains(user.Id);
        var adIds = (await db.UserLogins.Where(x => x.LoginProvider == "ActiveDirectory" && ids.Contains(x.UserId))
            .Select(x => x.UserId).ToListAsync()).ToHashSet();
        foreach (var user in result) user.IsAdManaged = adIds.Contains(user.Id);
        return result;
    }

    public async Task<bool> AnyUsersAsync()
    {
        return await userManager.Users.AnyAsync();
    }

    public async Task<ServiceResult<AppUser>> GetByIdAsync(int id)
    {
        var appUser = await userManager.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (appUser is not null)
        {
            var logins = await userManager.GetLoginsAsync(appUser);
            appUser.IsEntraManaged = logins.Any(x => x.LoginProvider == "Entra");
            appUser.IsAdManaged = logins.Any(x => x.LoginProvider == "ActiveDirectory");
        }

        return appUser is null
            ? ServiceResult<AppUser>.Fail(ErrorKeys.Common.NotFound, id)
            : ServiceResult<AppUser>.Ok(appUser);
    }

    public async Task<ServiceResult<AppUser>> CreateAsync(AppUser user, string password)
    {
        user.UserName = user.UserName?.Trim();
        user.Email = user.Email?.Trim();
        user.Membership = NormalizeMembership(user.Membership);
        user.EnableMfa = MfaPolicy.RequiresWeb(user);
        user.EmailConfirmed = true;

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
            return IdentityFailure(result);

        var claimResult = await UpdateMembershipClaimAsync(user);
        if (!claimResult.Succeeded)
            return ServiceResult<AppUser>.Fail(ErrorKeys.User.CreateIncomplete, user.UserName!, FormatErrors(claimResult));

        return ServiceResult<AppUser>.Ok(user);
    }

    public async Task<ServiceResult<AppUserOptions>> GetOptionsAsync(int userId)
    {
        await using var db = await dbFactory.CreateContextAsync();

        if (!await db.AppUsers.AnyAsync(x => x.Id == userId))
            return ServiceResult<AppUserOptions>.Fail(ErrorKeys.Common.NotFound, userId);

        var options = await db.AppUserOptions.AsNoTracking().FirstOrDefaultAsync(x => x.AppUserId == userId);
        return ServiceResult<AppUserOptions>.Ok(options ?? new AppUserOptions { AppUserId = userId });
    }

    public Task<ServiceResult> UpdateColorModeAsync(int userId, ColorMode colorMode) =>
        SaveOptionsAsync(userId, new AppUserOptions { ColorMode = colorMode }, false);

    public Task<ServiceResult> UpdateOptionsAsync(int userId, AppUserOptions updatedOptions) => SaveOptionsAsync(userId, updatedOptions, true);

    private async Task<ServiceResult> SaveOptionsAsync(int userId, AppUserOptions updatedOptions, bool saveLanguage)
    {
        if (saveLanguage && !UiLanguages.IsSupported(updatedOptions.Language)) return ServiceResult.Fail(LocKeys.User.UnsupportedLanguage);
        await using var db = await dbFactory.CreateContextAsync();

        if (!await db.AppUsers.AnyAsync(x => x.Id == userId))
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, userId);

        var options = await db.AppUserOptions.FirstOrDefaultAsync(x => x.AppUserId == userId);
        if (options is null)
        {
            options = new AppUserOptions { AppUserId = userId };
            db.AppUserOptions.Add(options);
        }

        options.ColorMode = updatedOptions.ColorMode;
        if (saveLanguage) options.Language = UiLanguages.Normalize(updatedOptions.Language);

        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<List<AuditLog>> GetAuditLogsAsync(int userId)
    {
        await using var db = await dbFactory.CreateContextAsync();

        return await db.AuditLogs.AsNoTracking().Where(x => x.AppUserId == userId)
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id).Take(200).ToListAsync();
    }

    public async Task<ServiceResult<AppUser>> UpdateAsync(AppUser user, string? password)
    {
        var existing = await userManager.FindByIdAsync(user.Id.ToString());

        if (existing is null)
            return ServiceResult<AppUser>.Fail(ErrorKeys.Common.NotFound, user.Id);

        var logins = await userManager.GetLoginsAsync(existing);
        var isAd = logins.Any(x => x.LoginProvider == "ActiveDirectory");
        if (isAd && !string.IsNullOrWhiteSpace(password))
            return ServiceResult<AppUser>.Fail(ErrorKeys.Common.IdentityError,
                UiText.Get(LocKeys.Ui.AppUserAdministration.ActiveDirectoryAccountsUseTheirADPasswordForWeb));

        var membership = NormalizeMembership(user.Membership);
        if (logins.Any(x => x.LoginProvider is "Entra" or "ActiveDirectory") &&
            membership != existing.Membership)
            return ServiceResult<AppUser>.Fail(ErrorKeys.Common.IdentityError,
                UiText.Get(LocKeys.Ui.AppUserAdministration.ManageThisAccountSMembershipThroughAppUserGroup));
        if (existing.Membership == Constants.UserMembership.Admin && membership != Constants.UserMembership.Admin)
        {
            var adminCount = await userManager.Users.CountAsync(x => x.Membership == Constants.UserMembership.Admin);
            if (adminCount == 1)
                return ServiceResult<AppUser>.Fail(ErrorKeys.User.LastAdminMembership);
        }

        // Reject invalid passwords before persisting any general settings.
        if (!string.IsNullOrWhiteSpace(password))
        {
            var errors = new List<IdentityError>();
            foreach (var validator in userManager.PasswordValidators)
            {
                var validation = await validator.ValidateAsync(userManager, user, password);
                if (!validation.Succeeded)
                    errors.AddRange(validation.Errors);
            }

            if (errors.Count > 0)
                return IdentityFailure(IdentityResult.Failed(errors.ToArray()));
        }

        if (isAd && await userManager.HasPasswordAsync(existing))
        {
            var removal = await userManager.RemovePasswordAsync(existing);
            if (!removal.Succeeded) return IdentityFailure(removal);
        }

        var administratorMfa = membership == Constants.UserMembership.Admin;
        if (!administratorMfa && logins.Any(x => x.LoginProvider is "Entra" or "ActiveDirectory"))
        {
            await using var db = await dbFactory.CreateContextAsync();
            var access = await DirectoryApplicationAccess.ForUserAsync(db, existing.Id);
            administratorMfa = access.Managed && access.Allowed && access.Administrator;
        }
        var securityChanged = existing.UserName != user.UserName?.Trim() ||
            existing.Membership != membership ||
            existing.EnableMfa != (user.EnableMfa || administratorMfa) ||
            existing.EnableImagingMfa != user.EnableImagingMfa ||
            existing.LockoutEnabled != user.LockoutEnabled;
        existing.UserName = user.UserName?.Trim();
        existing.Email = user.Email?.Trim();
        existing.Membership = membership;
        existing.EnableMfa = user.EnableMfa || administratorMfa;
        existing.EnableImagingMfa = user.EnableImagingMfa;
        existing.LockoutEnabled = user.LockoutEnabled;

        var updateResult = await userManager.UpdateAsync(existing);

        if (!updateResult.Succeeded)
            return IdentityFailure(updateResult);

        var claimResult = await UpdateMembershipClaimAsync(existing);
        if (!claimResult.Succeeded)
            return ServiceResult<AppUser>.Fail(ErrorKeys.User.UpdateIncomplete, FormatErrors(claimResult));

        if (securityChanged)
        {
            var stampResult = await userManager.UpdateSecurityStampAsync(existing);
            if (!stampResult.Succeeded)
                return ServiceResult<AppUser>.Fail(ErrorKeys.User.UpdateIncomplete, FormatErrors(stampResult));
        }

        if (existing.TwoFactorEnabled && !existing.EnableMfa && !existing.EnableImagingMfa)
        {
            await using var policyDb = await dbFactory.CreateContextAsync();
            var required = await MfaPolicy.RequiresWebAsync(policyDb, existing) || await MfaPolicy.RequiresImagingAsync(policyDb, existing);
            var mfaResult = required ? IdentityResult.Success : await userManager.SetTwoFactorEnabledAsync(existing, false);
            if (!mfaResult.Succeeded)
                return ServiceResult<AppUser>.Fail(ErrorKeys.User.UpdateIncomplete, FormatErrors(mfaResult));
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(existing);
            var passwordResult = await userManager.ResetPasswordAsync(existing, token, password);

            if (!passwordResult.Succeeded)
                return ServiceResult<AppUser>.Fail(ErrorKeys.User.UpdateIncomplete, FormatErrors(passwordResult));
        }

        // Return a detached result so subsequent form edits cannot mutate Identity's tracked user.
        return await GetByIdAsync(existing.Id);
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        var user = await userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, id);

        if (user.Membership == Constants.UserMembership.Admin)
        {
            var adminCount = await userManager.Users.CountAsync(x => x.Membership == Constants.UserMembership.Admin);
            if (adminCount == 1)
                return ServiceResult.Fail(ErrorKeys.User.LastAdminDelete);
        }

        var result = await userManager.DeleteAsync(user);
        return result.Succeeded
            ? ServiceResult.Ok()
            : ServiceResult.Fail(ErrorKeys.Common.IdentityError, FormatErrors(result));
    }

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        foreach (var id in ids.Distinct())
        {
            var result = await DeleteAsync(id);

            if (!result.Success)
                return result;
        }

        return ServiceResult.Ok();
    }

    private static ServiceResult<AppUser> IdentityFailure(IdentityResult result)
    {
        return ServiceResult<AppUser>.Fail(ErrorKeys.Common.IdentityError, FormatErrors(result));
    }

    private async Task<IdentityResult> UpdateMembershipClaimAsync(AppUser user)
    {
        var claims = await userManager.GetClaimsAsync(user);
        var membershipClaims = claims.Where(claim => claim.Type == nameof(AppUser.Membership)).ToList();

        if (membershipClaims.Count == 1 && membershipClaims[0].Value == user.Membership)
            return IdentityResult.Success;

        if (membershipClaims.Count > 0)
        {
            var result = await userManager.RemoveClaimsAsync(user, membershipClaims);
            if (!result.Succeeded)
                return result;
        }

        return await userManager.AddClaimAsync(user, new Claim(nameof(AppUser.Membership), user.Membership));
    }

    private static string NormalizeMembership(string? membership)
    {
        return string.Equals(membership, Constants.UserMembership.Admin, StringComparison.OrdinalIgnoreCase)
            ? Constants.UserMembership.Admin
            : Constants.UserMembership.User;
    }

    private static string FormatErrors(IdentityResult result)
    {
        return string.Join("; ", result.Errors.Select(x => x.Description));
    }
}
