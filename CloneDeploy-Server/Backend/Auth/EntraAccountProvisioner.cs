using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Auth;

// The principal must come from the validated OIDC external cookie, never request fields.
public sealed class EntraAccountProvisioner(CloneDeployDbContext db, UserManager<AppUser> users)
{
    public async Task<AppUser?> ProvisionAsync(ExternalLoginInfo info, CancellationToken ct = default)
    {
        // Identity and provisioning share this scoped context/transaction. Serialize first
        // logins and configuration changes, so a failed link cannot leave an orphan account.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        var settings = await db.Set<EntraConnection>().AsNoTracking().SingleOrDefaultAsync(ct);
        var key = EntraAuthentication.IdentityKey(settings, info.Principal);
        if (settings?.CertificateEncrypted is null || info.LoginProvider != "Entra" || key is null || key != info.ProviderKey)
            return null;

        var access = await DirectoryApplicationAccess.ForEntraAsync(db, key, ct);
        var membership = access.Administrator ? Constants.UserMembership.Admin : Constants.UserMembership.User;
        var user = await users.FindByLoginAsync("Entra", key);
        if (!access.Allowed)
        {
            if (user is not null)
            {
                await SetMembershipAsync(user, Constants.UserMembership.User);
                Ensure(await users.UpdateSecurityStampAsync(user));
            }
            await tx.CommitAsync(ct);
            return null;
        }

        var preferred = info.Principal.FindFirstValue("preferred_username")?.Trim();
        // A synchronized AD SID may link two authenticated directory identities; names never do.
        var oid = Guid.Parse(info.Principal.FindFirstValue("oid")!);
        var cloud = await db.DirectoryObjects.AsNoTracking().SingleOrDefaultAsync(x => x.ObjectGuid == oid &&
            x.DirectorySource.Provider == Backend.Enum.DirectoryProvider.EntraId && x.DirectorySource.SyncState.RootObjectGuid == settings.TenantId && !x.IsMissing, ct);
        if (cloud?.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true)
        {
            var ad = await db.DirectoryObjects.AsNoTracking().Where(x => x.ObjectSid == cloud.ObjectSid &&
                x.Kind == Backend.Enum.DirectoryObjectKind.User && !x.IsMissing && !x.Disabled &&
                x.DirectorySource.Provider == Backend.Enum.DirectoryProvider.ActiveDirectory && x.DirectorySource.IsEnabled).ToListAsync(ct);
            if (ad.Count == 1)
            {
                var linked = await users.FindByLoginAsync("ActiveDirectory", $"{ad[0].DirectorySourceId}/{ad[0].ObjectGuid:D}");
                if (user is not null && linked is not null && user.Id != linked.Id) return null;
                user ??= linked;
            }
        }
        var shortName = preferred?.Split('@')[0];
        var allowed = users.Options.User.AllowedUserNameCharacters;
        var validName = !string.IsNullOrWhiteSpace(shortName) && shortName.Length <= 180 &&
            (string.IsNullOrEmpty(allowed) || shortName.All(allowed.Contains));
        var email = EmailAddress(info.Principal.FindFirstValue("email")) ??
            (preferred?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true ? null : EmailAddress(preferred));

        if (user is null)
        {
            var name = validName ? shortName! : $"entra-{Guid.Parse(info.Principal.FindFirstValue("oid")!):N}";
            // A matching name/email never establishes ownership of a local account.
            if (await users.FindByNameAsync(name) is not null) name = $"{name}-{Guid.NewGuid():N}";
            user = new AppUser { UserName = name, Email = email, Membership = membership, LockoutEnabled = true };
            Ensure(await users.CreateAsync(user));
            Ensure(await users.AddLoginAsync(user, new UserLoginInfo("Entra", key, "Microsoft Entra ID")));
        }
        else
        {
            var changed = false;
            // Shorten names created by the previous provisioning flow, preserving custom names.
            if (validName && preferred!.Contains('@') && string.Equals(user.UserName, preferred, StringComparison.OrdinalIgnoreCase) &&
                await users.FindByNameAsync(shortName!) is null)
            {
                user.UserName = shortName;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(user.Email) && email is not null)
            {
                user.Email = email;
                user.EmailConfirmed = false;
                changed = true;
            }
            if (changed) Ensure(await users.UpdateAsync(user));
        }
        if (!(await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "Entra" && x.ProviderKey == key))
            Ensure(await users.AddLoginAsync(user, new UserLoginInfo("Entra", key, "Microsoft Entra ID")));
        await SetMembershipAsync(user, membership);
        await tx.CommitAsync(ct);
        return user;
    }

    private static string? EmailAddress(string? value)
    {
        value = value?.Trim();
        return value is { Length: <= 256 } && System.Net.Mail.MailAddress.TryCreate(value, out var address) &&
            address.Address == value && value.Contains('@') ? value : null;
    }

    private async Task SetMembershipAsync(AppUser user, string membership)
    {
        var claims = (await users.GetClaimsAsync(user)).Where(c => c.Type == nameof(AppUser.Membership)).ToArray();
        if (user.Membership == membership && claims.Length == 1 && claims[0].Value == membership) return;
        user.Membership = membership;
        Ensure(await users.UpdateAsync(user));
        if (claims.Length > 0) Ensure(await users.RemoveClaimsAsync(user, claims));
        Ensure(await users.AddClaimAsync(user, new Claim(nameof(AppUser.Membership), membership)));
        Ensure(await users.UpdateSecurityStampAsync(user));
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Entra account provisioning failed: " +
            string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
