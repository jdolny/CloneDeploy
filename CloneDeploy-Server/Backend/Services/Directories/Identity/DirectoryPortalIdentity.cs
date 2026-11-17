using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Matches a portal caller's Windows SID to one active synced user, never by account name.</summary>
public static class DirectoryPortalIdentity
{
    public static async Task<AgentPortalIdentity> ResolveAsync(CloneDeployDbContext db, string sid, CancellationToken ct)
    {
        var (status, users) = await DirectoryIdentity.UsersAsync(db, sid, ct);
        if (status != "Matched") return new(status);
        var user = users[0];
        return new("Matched", new(user.DirectorySourceId, user.ObjectGuid, user.Name,
            user.UserPrincipalName, user.DirectorySource.Name));
    }
}
