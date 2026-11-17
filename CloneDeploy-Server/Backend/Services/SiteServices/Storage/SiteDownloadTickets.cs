using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SiteDownloadTickets(ICloneDeployDbFactory factory, IDataProtectionProvider protection, TimeProvider clock)
{
    private sealed record Ticket(Guid Site, string Address, DateTimeOffset Expires, SiteDownloadOffer Offer);
    private readonly IDataProtector protector = protection.CreateProtector("CloneDeploy.SiteDownload.v1");

    public async Task<SiteDirectDownload> IssueAsync(SiteDownloadOffer offer, IPAddress address, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var provider = await db.StorageProviders.AsNoTracking().Include(x => x.SiteService).SingleAsync(x => x.Id == offer.ProviderId, ct);
        var site = provider.SiteService ?? throw new InvalidDataException("Not a site provider.");
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site.Id, ct)).StorageEnabled || !provider.IsEnabled || site.State != SiteRegistrationState.Approved || !site.ProxyEnabled || site.HttpsCertificate is null)
            throw new UnauthorizedAccessException();
        var ticket = protector.Protect(JsonSerializer.Serialize(new Ticket(site.Id, Normalize(address), clock.GetUtcNow().AddSeconds(60), offer)));
        return new(new Uri(new Uri(site.PublicUrl), SiteDirectDownload.Path).AbsoluteUri, site.ListenAddress,
            Convert.ToHexString(SHA256.HashData(site.HttpsCertificate)), ticket);
    }

    public async Task<SiteDownloadOffer> RedeemAsync(Guid site, SiteDownloadRedemption request, CancellationToken ct)
    {
        if (request.Ticket is not { Length: > 0 and <= 8192 } || !IPAddress.TryParse(request.ClientAddress, out var address)) throw new UnauthorizedAccessException();
        Ticket ticket;
        try { ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(request.Ticket)) ?? throw new UnauthorizedAccessException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new UnauthorizedAccessException(); }
        if (ticket.Site != site || ticket.Expires <= clock.GetUtcNow() || ticket.Address != Normalize(address)) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        if (!(await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).StorageEnabled) throw new UnauthorizedAccessException();
        var offer = ticket.Offer;
        var key = offer.Key.Replace("shared-", "shared:").Replace("image-", "image:");
        if (!await db.StorageProviders.AnyAsync(p => p.Id == offer.ProviderId && p.IsEnabled && p.SiteServiceId == site &&
                p.SiteService!.State == SiteRegistrationState.Approved && p.SiteService.ProxyEnabled, ct) ||
            !await db.Set<StorageReadLease>().AnyAsync(x => x.Id == offer.LeaseId && x.StorageProviderId == offer.ProviderId &&
                x.ContentKey == key && x.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime, ct)) throw new UnauthorizedAccessException();
        return offer;
    }

    private static string Normalize(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    public async Task AbandonAsync(string value, IPAddress address, CancellationToken ct)
    {
        if (value is not { Length: > 0 and <= 8192 }) throw new UnauthorizedAccessException();
        Ticket ticket;
        try { ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(value)) ?? throw new UnauthorizedAccessException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new UnauthorizedAccessException(); }
        if (ticket.Expires <= clock.GetUtcNow() || ticket.Address != Normalize(address)) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", ct);
        var removed = await db.Set<StorageReadLease>().Where(x => x.Id == ticket.Offer.LeaseId && x.StorageProviderId == ticket.Offer.ProviderId).ExecuteDeleteAsync(ct);
        if (removed != 0) await db.Set<AgentDownloadLease>().Where(x => x.Id == ticket.Offer.LeaseId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }
}
