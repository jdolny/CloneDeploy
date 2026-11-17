using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Localization;

public sealed class LocalizedIdentityErrors : IdentityErrorDescriber
{
    public override IdentityError DefaultError()
    {
        var error = base.DefaultError();
        error.Description = UiText.Get(LocKeys.Identity.DefaultError);
        return error;
    }

    public override IdentityError ConcurrencyFailure()
    {
        var error = base.ConcurrencyFailure();
        error.Description = UiText.Get(LocKeys.Identity.ConcurrencyFailure);
        return error;
    }

    public override IdentityError PasswordMismatch()
    {
        var error = base.PasswordMismatch();
        error.Description = UiText.Get(LocKeys.Identity.PasswordMismatch);
        return error;
    }

    public override IdentityError InvalidToken()
    {
        var error = base.InvalidToken();
        error.Description = UiText.Get(LocKeys.Identity.InvalidToken);
        return error;
    }

    public override IdentityError RecoveryCodeRedemptionFailed()
    {
        var error = base.RecoveryCodeRedemptionFailed();
        error.Description = UiText.Get(LocKeys.Identity.RecoveryCodeRedemptionFailed);
        return error;
    }

    public override IdentityError LoginAlreadyAssociated()
    {
        var error = base.LoginAlreadyAssociated();
        error.Description = UiText.Get(LocKeys.Identity.LoginAlreadyAssociated);
        return error;
    }

    public override IdentityError InvalidUserName(string? userName)
    {
        var error = base.InvalidUserName(userName);
        error.Description = UiText.Get(LocKeys.Identity.InvalidUserName, userName);
        return error;
    }

    public override IdentityError InvalidEmail(string? email)
    {
        var error = base.InvalidEmail(email);
        error.Description = UiText.Get(LocKeys.Identity.InvalidEmail, email);
        return error;
    }

    public override IdentityError DuplicateUserName(string userName)
    {
        var error = base.DuplicateUserName(userName);
        error.Description = UiText.Get(LocKeys.Identity.DuplicateUserName, userName);
        return error;
    }

    public override IdentityError DuplicateEmail(string email)
    {
        var error = base.DuplicateEmail(email);
        error.Description = UiText.Get(LocKeys.Identity.DuplicateEmail, email);
        return error;
    }

    public override IdentityError InvalidRoleName(string? role)
    {
        var error = base.InvalidRoleName(role);
        error.Description = UiText.Get(LocKeys.Identity.InvalidRoleName, role);
        return error;
    }

    public override IdentityError DuplicateRoleName(string role)
    {
        var error = base.DuplicateRoleName(role);
        error.Description = UiText.Get(LocKeys.Identity.DuplicateRoleName, role);
        return error;
    }

    public override IdentityError UserAlreadyHasPassword()
    {
        var error = base.UserAlreadyHasPassword();
        error.Description = UiText.Get(LocKeys.Identity.UserAlreadyHasPassword);
        return error;
    }

    public override IdentityError UserLockoutNotEnabled()
    {
        var error = base.UserLockoutNotEnabled();
        error.Description = UiText.Get(LocKeys.Identity.UserLockoutNotEnabled);
        return error;
    }

    public override IdentityError UserAlreadyInRole(string role)
    {
        var error = base.UserAlreadyInRole(role);
        error.Description = UiText.Get(LocKeys.Identity.UserAlreadyInRole, role);
        return error;
    }

    public override IdentityError UserNotInRole(string role)
    {
        var error = base.UserNotInRole(role);
        error.Description = UiText.Get(LocKeys.Identity.UserNotInRole, role);
        return error;
    }

    public override IdentityError PasswordTooShort(int length)
    {
        var error = base.PasswordTooShort(length);
        error.Description = UiText.Get(LocKeys.Identity.PasswordTooShort, length);
        return error;
    }

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars)
    {
        var error = base.PasswordRequiresUniqueChars(uniqueChars);
        error.Description = UiText.Get(LocKeys.Identity.PasswordRequiresUniqueChars, uniqueChars);
        return error;
    }

    public override IdentityError PasswordRequiresNonAlphanumeric()
    {
        var error = base.PasswordRequiresNonAlphanumeric();
        error.Description = UiText.Get(LocKeys.Identity.PasswordRequiresNonAlphanumeric);
        return error;
    }

    public override IdentityError PasswordRequiresDigit()
    {
        var error = base.PasswordRequiresDigit();
        error.Description = UiText.Get(LocKeys.Identity.PasswordRequiresDigit);
        return error;
    }

    public override IdentityError PasswordRequiresLower()
    {
        var error = base.PasswordRequiresLower();
        error.Description = UiText.Get(LocKeys.Identity.PasswordRequiresLower);
        return error;
    }

    public override IdentityError PasswordRequiresUpper()
    {
        var error = base.PasswordRequiresUpper();
        error.Description = UiText.Get(LocKeys.Identity.PasswordRequiresUpper);
        return error;
    }

}
