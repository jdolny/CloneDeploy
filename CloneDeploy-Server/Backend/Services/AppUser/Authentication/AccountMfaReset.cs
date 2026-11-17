using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Administrative recovery only. Enrollment and policy are deliberately separate.
public sealed class AccountMfaReset(IServiceScopeFactory scopes, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<AccountMfaReset> logger)
{
    public Task<ServiceResult> ResetAsync(int userId) => ServiceOperations.RunAsync(async () =>
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true ||
            !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) || actorId == userId ||
            !(await authorization.AuthorizeAsync(principal, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult.Fail(ServiceErrorCodes.Forbidden);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloneDeployDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var actor = await users.FindByIdAsync(actorId.ToString());
        if (actor is null || string.IsNullOrEmpty(actor.SecurityStamp) || await users.IsLockedOutAsync(actor) ||
            principal.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType) != actor.SecurityStamp)
            return ServiceResult.Fail(ServiceErrorCodes.Forbidden);

        await using var transaction = await db.Database.BeginTransactionAsync();
        // Serialize against enrollment/replacement so an in-flight setup cannot undo a reset.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AppUsers\" WHERE \"Id\" = {userId} FOR UPDATE");
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return ServiceResult.Fail("Common.NotFound", userId);
        Require(await users.SetTwoFactorEnabledAsync(user, false));
        Require(await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey"));
        Require(await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes"));
        Require(await users.UpdateSecurityStampAsync(user));
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = actorId, UserName = actor.UserName ?? "Administrator", AuditType = AuditType.Update,
            ObjectType = nameof(AppUser), ObjectId = user.Id, ObjectName = user.UserName ?? user.Id.ToString(),
            ObjectJson = "{\"Operation\":\"ResetMfa\"}"
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }, logger, "User.OperationFailed");

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Could not reset authenticator enrollment.");
    }
}
