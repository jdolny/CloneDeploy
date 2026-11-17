using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Selection is shared with main storage; transport is chosen only after reserving a provider.
public sealed class SiteDownloadDelivery(ICloneDeployDbFactory factory, IHttpContextAccessor requests)
{
    public StorageReadSelection Selection { get; init; } = null!;
    public const string PathMarker = "site-replica";
    public static readonly object OfferKey = new();
    public static readonly object DownloadSlotKey = new();
    public static readonly object TransferKey = new();
    public static readonly object PreserveLeaseKey = new();
    public static bool HasOffer(HttpContext? context) => context?.Items.ContainsKey(OfferKey) == true;

    public async Task<Stream?> TryOpenAsync(string key, long length, string hash, StoragePurpose purpose, CancellationToken ct)
    {
        var context = requests.HttpContext;
        if (context is null || !HttpMethods.IsGet(context.Request.Method)) return null;
        await using var db = await factory.CreateContextAsync();
        var ids = await ProvidersAsync(db, key, ct);
        var leaseId = context.Items.TryGetValue(TransferKey, out var pinned) && pinned is Guid transfer ? transfer :
            context.Items.TryGetValue(DownloadSlotKey, out var slot) && slot is Guid id ? id : Guid.NewGuid();
        context.Items[TransferKey] = leaseId;
        var preserve = context.Items.ContainsKey(PreserveLeaseKey);
        int provider;
        try
        {
            provider = await Selection.ReserveAsync(leaseId, key.Replace("shared-", "shared:").Replace("image-", "image:"), purpose,
                ids, context.Connection.RemoteIpAddress, TimeSpan.FromMinutes(preserve ? 15 : 2), ct);
        }
        catch (IOException) { return null; } // Existing main/legacy path supplies its normal error or fallback.
        if (!await db.StorageProviders.AnyAsync(x => x.Id == provider && x.SiteServiceId != null, ct)) return null;
        // ProvidersAsync already loaded this entity into this context; FindAsync reuses it without another query.
        var relativePath = key.StartsWith("shared-")
            ? (await db.Set<SharedContent>().FindAsync([key[7..]], ct))!.ReplicaRelativePath
            : $"images/{key[6..]}/{(await db.ImageVersions.FindAsync([Guid.ParseExact(key[6..], "N")], ct))!.FileName}";
        context.Items[OfferKey] = new SiteDownloadOffer(provider, key, length, hash, leaseId, "application/octet-stream", false, null)
        { MaximumSeconds = context.Items.ContainsKey(DownloadSlotKey) ? 7200 : 0, PreserveLease = preserve, RelativePath = relativePath };
        return new SiteDownloadStream(length);
    }

    public async Task<bool> HasSharedAsync(SharedContentKind kind, string key, string hash, CancellationToken ct) =>
        await HasEligibleSiteAsync("shared-" + SharedContentInventory.Identity(kind, key, hash), ct);

    private async Task<bool> HasEligibleSiteAsync(string key, CancellationToken ct)
    {
        if (requests.HttpContext is not { } context) return false;
        await using var db = await factory.CreateContextAsync();
        var ids = await ProvidersAsync(db, key, ct);
        var providers = await db.StorageProviders.AsNoTracking().Include(x => x.SiteService).ThenInclude(x => x!.Issuer)
            .Where(x => ids.Contains(x.Id) && x.IsEnabled && x.SiteServiceId != null).ToListAsync(ct);
        foreach (var provider in providers)
            if ((await SiteServiceAdministration.ReadProxyAccessAsync(db, provider.SiteServiceId!.Value, ct)).StorageEnabled && Selection.AllowsSite(provider, context.Connection.RemoteIpAddress)) return true;
        return false;
    }

    private static async Task<List<int>> ProvidersAsync(CloneDeployDbContext db, string key, CancellationToken ct)
    {
        if (key.StartsWith("image-"))
        {
            var version = await db.ImageVersions.SingleAsync(x => x.Id == Guid.ParseExact(key.Substring(6), "N"), ct);
            return await ImageReplicaCatalog.Providers(db, version).Select(x => x.Id).ToListAsync(ct);
        }
        var content = await db.Set<SharedContent>().SingleAsync(x => x.Id == key.Substring(7), ct);
        var ids = await db.Set<SharedContentReplica>().Where(x => x.ContentId == content.Id && x.State == ImageReplicaState.Ready)
            .Select(x => x.StorageProviderId).ToListAsync(ct);
        if (content.SourceProviderId is { } primary) ids.Add(primary);
        return ids;
    }
}

// Controllers may inspect length but must never try reading this marker locally.
public sealed class SiteDownloadStream(long length) : MemoryStream
{
    public override long Length => length;
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) => throw new InvalidOperationException("Site handoff must be handled by the response filter.");
}

public sealed class SiteDownloadResultFilter(ICloneDeployDbFactory factory, SiteDownloadTickets tickets) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!context.HttpContext.Items.TryGetValue(SiteDownloadDelivery.OfferKey, out var value) || value is not SiteDownloadOffer offer) { await next(); return; }
        if (context.Result is FileResult or EmptyResult)
        {
            if (context.Result is FileResult file)
                offer = offer with { ContentType = file.ContentType, Ranges = file.EnableRangeProcessing, FileName = file.FileDownloadName };
            if (context.Result is FileStreamResult stream) await stream.FileStream.DisposeAsync();
            if (TrustedSiteProxy.IsProxied(context.HttpContext) && !ImagerSessionMiddleware.IsAuthenticated(context.HttpContext))
                context.HttpContext.Response.Headers[SiteStorageProtocol.OfferHeader] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(offer));
            else
            {
                var direct = await tickets.IssueAsync(offer, context.HttpContext.Connection.RemoteIpAddress ?? throw new UnauthorizedAccessException(), context.HttpContext.RequestAborted);
                context.HttpContext.Response.Headers[CloneDeploy_Agent.Contracts.SiteDirectDownload.OfferHeader] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(direct));
            }
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.ContentLength = null;
            context.Result = new NoContentResult();
        }
        else
        {
            await using var db = await factory.CreateContextAsync();
            await db.Set<StorageReadLease>().Where(x => x.Id == offer.LeaseId).ExecuteDeleteAsync();
            await db.Set<AgentDownloadLease>().Where(x => x.Id == offer.LeaseId).ExecuteDeleteAsync();
        }
        await next();
    }
}
