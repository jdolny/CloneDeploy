using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record SharedReplicationStatus(string Name, string Kind, string Provider, ImageReplicaState State, string? Error);
public sealed record SharedReplicationCount(ImageReplicaState State, int Count);
public sealed record ReplicationActivity(string Name, string Kind, string Provider, string Host, bool Enabled,
    ImageReplicaState State, DateTime UpdatedAtUtc, DateTime? RetryAtUtc, string? Error);
public sealed record ReplicationOverview(List<SharedReplicationCount> Counts, List<ReplicationActivity> Jobs, DateTime CheckedAtUtc);
public sealed record SharedReplicationSettings(List<StorageProvider> Providers, List<int> Destinations, List<SharedReplicationStatus> Copies);
public sealed class SharedReplicationAdministration(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public async Task<ServiceResult<ReplicationOverview>> ReadActivityAsync(CancellationToken ct = default)
    {
        if (await AdminAsync() is null) return ServiceResult<ReplicationOverview>.Fail("StorageMount.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var shared = from r in db.Set<SharedContentReplica>()
            join c in db.Set<SharedContent>() on r.ContentId equals c.Id
            join p in db.StorageProviders on r.StorageProviderId equals p.Id
            select new { Name = c.Name, Kind = "Shared file", Provider = p.Name, Host = p.SiteService == null ? "Main server" : p.SiteService.Name,
                Enabled = p.IsEnabled, r.State, r.UpdatedAtUtc, r.RetryAtUtc, r.Error };
        var images = from r in db.Set<ImageReplica>()
            join v in db.ImageVersions on r.ImageVersionId equals v.Id into versions
            from v in versions.DefaultIfEmpty()
            join p in db.StorageProviders on r.StorageProviderId equals p.Id
            select new { Name = v == null ? "Deleted image version" : v.Name + " / " + v.FileName, Kind = "Image", Provider = p.Name,
                Host = p.SiteService == null ? "Main server" : p.SiteService.Name, Enabled = p.IsEnabled,
                r.State, r.UpdatedAtUtc, r.RetryAtUtc, r.Error };
        var all = shared.Concat(images);
        var counts = await all.GroupBy(x => x.State).Select(g => new SharedReplicationCount(g.Key, g.Count())).ToListAsync(ct);
        var jobs = await all.Where(x => x.State != ImageReplicaState.Ready)
            .OrderByDescending(x => x.State == ImageReplicaState.Copying || x.State == ImageReplicaState.Verifying)
            .ThenByDescending(x => x.State == ImageReplicaState.Failed || x.Error != null)
            .ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Provider).ThenBy(x => x.Name).Take(100)
            .Select(x => new ReplicationActivity(x.Name, x.Kind, x.Provider, x.Host, x.Enabled, x.State, x.UpdatedAtUtc, x.RetryAtUtc, x.Error)).ToListAsync(ct);
        return ServiceResult<ReplicationOverview>.Ok(new(counts, jobs, DateTime.UtcNow));
    }

    public async Task<ServiceResult<SharedReplicationSettings>> ReadAsync()
    {
        if (await AdminAsync() is null) return ServiceResult<SharedReplicationSettings>.Fail("StorageMount.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var targets = await db.StorageProviders.Where(x => x.SiteServiceId == null).Select(x => x.Id).ToListAsync();
        var providers = await db.StorageProviders.AsNoTracking().Where(x => targets.Contains(x.Id) || x.IsEnabled && x.SiteServiceId == null).OrderBy(x => x.Name).ToListAsync();
        var copies = await (from r in db.Set<SharedContentReplica>() join c in db.Set<SharedContent>() on r.ContentId equals c.Id
            join p in db.StorageProviders on r.StorageProviderId equals p.Id
            orderby r.State == ImageReplicaState.Ready, c.Name
            select new SharedReplicationStatus(c.Name, c.Kind.ToString(), p.Name, r.State, r.Error)).Take(500).ToListAsync();
        return ServiceResult<SharedReplicationSettings>.Ok(new(providers, targets, copies));
    }

    private async Task<ClaimsPrincipal?> AdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _) ? user : null;
    }
}
