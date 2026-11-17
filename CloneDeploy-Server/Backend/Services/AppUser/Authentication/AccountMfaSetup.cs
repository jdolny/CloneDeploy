using System.Security.Claims;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Pending secrets belong only to this browser circuit. Reads never alter Identity state.
public sealed class AccountMfaSetup(IServiceScopeFactory scopes, AuthenticationStateProvider authentication, SecurityActivity? security = null)
{
    public string? PendingKey { get; private set; }
    public string? AccountName { get; private set; }
    public bool Enabled { get; private set; }
    private string? expectedStamp;
    private DateTimeOffset expiresAt;
    private readonly SemaphoreSlim gate = new(1, 1);

    private async Task<AppUser> CurrentAsync(UserManager<AppUser> users)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var user = principal.Identity?.IsAuthenticated == true ? await users.GetUserAsync(principal) : null;
        if (user is null || await users.IsLockedOutAsync(user) ||
            principal.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType) != user.SecurityStamp)
            throw new UnauthorizedAccessException("Sign in again before managing your authenticator.");
        return user;
    }

    public async Task LoadAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await CurrentAsync(users);
        AccountName = user.UserName;
        Enabled = user.TwoFactorEnabled;
        if (!Enabled && PendingKey is null) Prepare(users, user);
    }

    private void Prepare(UserManager<AppUser> users, AppUser user)
    {
        PendingKey = users.GenerateNewAuthenticatorKey();
        expectedStamp = user.SecurityStamp;
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
    }

    public async Task<bool> BeginReplacementAsync(string code)
    {
        await gate.WaitAsync();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await CurrentAsync(users);
            if (user.TwoFactorEnabled)
            {
                var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));
                if (!valid) { await users.AccessFailedAsync(user); return false; }
                await users.ResetAccessFailedCountAsync(user);
            }
            Prepare(users, user);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> VerifyAsync(string code)
    {
        await gate.WaitAsync();
        try
        {
            if (PendingKey is null || DateTimeOffset.UtcNow > expiresAt) return false;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloneDeployDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var store = (IUserAuthenticatorKeyStore<AppUser>)scope.ServiceProvider.GetRequiredService<IUserStore<AppUser>>();
            var user = await CurrentAsync(users);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AppUsers\" WHERE \"Id\" = {user.Id} FOR UPDATE");
            await db.Entry(user).ReloadAsync();
            if (user.SecurityStamp != expectedStamp || await users.IsLockedOutAsync(user)) return false;
            // Stage the pending key in this transaction so Identity's own TOTP validator can check it.
            // Invalid codes roll back the key; the currently enrolled authenticator stays usable.
            await store.SetAuthenticatorKeyAsync(user, PendingKey, CancellationToken.None);
            if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code)))
            {
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                var fresh = await users.FindByIdAsync(user.Id.ToString());
                if (fresh is not null) await users.AccessFailedAsync(fresh);
                return false;
            }
            user.EnableMfa = true;
            Require(await users.SetTwoFactorEnabledAsync(user, true));
            Require(await users.UpdateSecurityStampAsync(user));
            Require(await users.ResetAccessFailedCountAsync(user));
            await transaction.CommitAsync();
            if (security is not null) await security.ChangeAsync("Authenticator enrolled or replaced", $"User #{user.Id}: {user.UserName}");
            Enabled = true;
            PendingKey = null;
            return true;
        }
        finally { gate.Release(); }
    }

    private static string Normalize(string code) => code.Replace(" ", "").Replace("-", "");
    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Could not save authenticator settings.");
    }
}
