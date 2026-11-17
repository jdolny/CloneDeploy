using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record AdLoginResult(bool Handled, AppUser? User = null, string? Key = null, bool IsLockedOut = false);

public sealed class AdLoginService(CloneDeployDbContext db, UserManager<AppUser> users, IAdPasswordValidator validator)
{
    public async Task<AdLoginResult> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        var local = await users.FindByNameAsync(username);
        var logins = local is null ? [] : await users.GetLoginsAsync(local);
        // AD-linked accounts always validate against AD, even if an older local hash exists.
        // Keep local and Entra Imager passwords on their own credential path.
        if (local is not null && !logins.Any(x => x.LoginProvider == "ActiveDirectory") &&
            (await users.HasPasswordAsync(local) || !logins.Any(x => x.LoginProvider == "Entra"))) return new(false);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return new(true);
        var parts = username.Split('\\');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace)) return new(true);
        var domain = parts.Length == 2 ? parts[0] : null;
        var account = parts[^1];
        var sources = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).Include(x => x.SyncState)
            .Where(x => x.Provider == DirectoryProvider.ActiveDirectory && x.IsEnabled && x.SyncState != null).ToListAsync(ct);
        if (sources.Count != 1) return new(true);
        var source = sources[0];
        var candidates = db.DirectoryObjects.AsNoTracking().Where(x => x.DirectorySourceId == source.Id && x.Kind == DirectoryObjectKind.User && !x.IsMissing && !x.Disabled);
        var linked = logins.SingleOrDefault(x => x.LoginProvider == "ActiveDirectory");
        if (linked is not null)
        {
            var keyParts = linked.ProviderKey.Split('/');
            if (keyParts.Length != 2 || keyParts[0] != source.Id.ToString() || !Guid.TryParse(keyParts[1], out var id)) return new(true);
            candidates = candidates.Where(x => x.ObjectGuid == id);
        }
        else if (account.Contains('@')) candidates = candidates.Where(x => x.UserPrincipalName != null && x.UserPrincipalName.ToLower() == account.ToLower());
        else candidates = candidates.Where(x => x.AccountName != null && x.AccountName.ToLower() == account.ToLower());
        var matches = await candidates.ToListAsync(ct);
        if (matches.Count != 1) return new(true);
        var identity = matches[0];
        var key = $"{source.Id}/{identity.ObjectGuid:D}";
        var user = await users.FindByLoginAsync("ActiveDirectory", key);
        var correlated = await FindEntraAccountAsync(identity, ct);
        if (user is not null && correlated is not null && user.Id != correlated.Id) return new(true);
        user ??= correlated;
        // Matching usernames alone cannot attach an AD identity to an existing application account.
        if (local is not null && user?.Id != local.Id) return new(true);
        if (user is not null && await users.IsLockedOutAsync(user)) return new(true, IsLockedOut: true);
        if (!(await DirectoryApplicationAccess.ForAdAsync(db, key, ct)).Allowed) return new(true);
        if (!await validator.ValidateAsync(source, identity, domain, password, ct))
        {
            if (user is not null) Ensure(await users.AccessFailedAsync(user));
            return new(true, IsLockedOut: user is not null && await users.IsLockedOutAsync(user));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        var current = await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).Include(x => x.SyncState).SingleOrDefaultAsync(x => x.Id == source.Id, ct);
        if (current?.IsEnabled != true || current.LdapSettings?.Host != source.LdapSettings?.Host || current.LdapSettings?.Port != source.LdapSettings?.Port ||
            current.SyncState?.RootObjectGuid != source.SyncState.RootObjectGuid || current.SyncState?.DomainObjectGuid != source.SyncState.DomainObjectGuid) return new(true);
        var currentIdentity = await db.DirectoryObjects.AsNoTracking().SingleOrDefaultAsync(x => x.DirectorySourceId == source.Id && x.ObjectGuid == identity.ObjectGuid, ct);
        if (currentIdentity is null || currentIdentity.ObjectSid != identity.ObjectSid || currentIdentity.DistinguishedName != identity.DistinguishedName) return new(true);
        var access = await DirectoryApplicationAccess.ForAdAsync(db, key, ct);
        if (!access.Allowed) return new(true);
        user = await users.FindByLoginAsync("ActiveDirectory", key) ?? await FindEntraAccountAsync(identity, ct);
        if (local is not null && user?.Id != local.Id) return new(true);
        if (user is not null)
        {
            await db.Entry(user).ReloadAsync(ct);
            if (await users.IsLockedOutAsync(user)) return new(true, IsLockedOut: true);
        }
        var membership = access.Administrator ? Constants.UserMembership.Admin : Constants.UserMembership.User;
        if (user is null)
        {
            var name = identity.AccountName;
            var allowed = users.Options.User.AllowedUserNameCharacters;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || (!string.IsNullOrEmpty(allowed) && !name.All(allowed.Contains))) name = "ad-" + identity.ObjectGuid.ToString("N");
            if (await users.FindByNameAsync(name) is not null) name += "-" + Guid.NewGuid().ToString("N");
            user = new AppUser { UserName = name, Email = identity.Email, Membership = membership, LockoutEnabled = true };
            Ensure(await users.CreateAsync(user));
        }
        if (!(await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == "ActiveDirectory" && x.ProviderKey == key))
            Ensure(await users.AddLoginAsync(user, new UserLoginInfo("ActiveDirectory", key, "Active Directory")));
        if (await users.HasPasswordAsync(user)) Ensure(await users.RemovePasswordAsync(user));
        var claims = (await users.GetClaimsAsync(user)).Where(x => x.Type == nameof(AppUser.Membership)).ToArray();
        if (user.Membership != membership || claims.Length != 1 || claims[0].Value != membership)
        {
            user.Membership = membership;
            Ensure(await users.UpdateAsync(user));
            if (claims.Length > 0) Ensure(await users.RemoveClaimsAsync(user, claims));
            Ensure(await users.AddClaimAsync(user, new Claim(nameof(AppUser.Membership), membership)));
            Ensure(await users.UpdateSecurityStampAsync(user));
        }
        await tx.CommitAsync(ct);
        return new(true, user, key);
    }

    private async Task<AppUser?> FindEntraAccountAsync(DirectoryObject identity, CancellationToken ct)
    {
        if (identity.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) != true) return null;
        var matches = await db.DirectoryObjects.AsNoTracking().Where(x => x.Kind == DirectoryObjectKind.User && x.ObjectSid == identity.ObjectSid &&
            !x.IsMissing && !x.Disabled && x.DirectorySource.Provider == DirectoryProvider.EntraId && x.DirectorySource.IsEnabled &&
            db.EntraConnections.Any(e => e.TenantId == x.DirectorySource.SyncState.RootObjectGuid))
            .Select(x => new { x.ObjectGuid, Tenant = x.DirectorySource.SyncState.RootObjectGuid }).ToListAsync(ct);
        return matches.Count == 1 ? await users.FindByLoginAsync("Entra", $"{matches[0].Tenant:D}/{matches[0].ObjectGuid:D}") : null;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("AD account provisioning failed: " + string.Join(", ", result.Errors.Select(x => x.Code)));
    }
}
