using System.Net.Http.Json;
using System.Text.Json;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

public sealed class SiteDownloadResponse(SiteStorageCache cache, SiteLocalState state)
{
    private const int MaximumTicketCharacters = 8192;
    private const int MaximumOfferCharacters = 8192;
    private const int MaximumDownloadSeconds = 7200;
    private static readonly TimeSpan RedemptionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(30);

    public async Task ServeDirectAsync(HttpContext context)
    {
        var token = context.Request.Headers[CloneDeploy_Agent.Contracts.SiteDirectDownload.TicketHeader];
        if (!HttpMethods.IsGet(context.Request.Method) || token.Count != 1 || token.ToString().Length is < 1 or > MaximumTicketCharacters || context.Connection.RemoteIpAddress is not { } address)
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        using var client = SiteEnrollmentClient.CreateClient(state, true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(RedemptionTimeout);
        try
        {
            using var response = await client.PostAsJsonAsync("/api/site-services/storage/redeem", new SiteDownloadRedemption(token.ToString(), address.ToString()), deadline.Token);
            if (!response.IsSuccessStatusCode) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            var offer = await response.Content.ReadFromJsonAsync<SiteDownloadOffer>(deadline.Token) ?? throw new InvalidDataException("Missing download authorization.");
            if (!await ServeAsync(context, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(offer)))) context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is HttpRequestException or IOException or OperationCanceledException)
        { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; }
    }

    // The offer arrives only from the authenticated backend response for this exact request.
    public async Task<bool> ServeAsync(HttpContext context, string encoded)
    {
        if (encoded.Length > MaximumOfferCharacters) throw new InvalidDataException("Invalid site download offer.");
        var offer = JsonSerializer.Deserialize<SiteDownloadOffer>(Convert.FromBase64String(encoded)) ?? throw new InvalidDataException("Invalid site download offer.");
        if (offer.Length < 0 || !SiteStorageProtocol.ValidKey(offer.Key) || offer.Sha256 is not { Length: SiteProxyProtocol.Sha256HexCharacters } || !offer.Sha256.All(Uri.IsHexDigit) || offer.LeaseId == Guid.Empty || offer.MaximumSeconds is < 0 or > MaximumDownloadSeconds)
            throw new InvalidDataException("Invalid site download offer.");
        using var client = SiteEnrollmentClient.CreateClient(state, true);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (offer.MaximumSeconds > 0) lifetime.CancelAfter(TimeSpan.FromSeconds(offer.MaximumSeconds));
        Task? renew = null;
        try
        {
            await RenewAsync(client, offer.LeaseId, lifetime.Token);
            renew = RenewLoopAsync(client, offer.LeaseId, lifetime);
            Stream stream;
            try { stream = await cache.OpenReadAsync(state.Configuration!.RegistrationId, offer, lifetime.Token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                using var report = await client.PostAsJsonAsync("/api/site-services/storage/report",
                    new SiteStorageReport(offer.ProviderId, offer.Key, offer.Length, offer.Sha256, "Failed", "Local replica was unavailable during a client download."), lifetime.Token);
                return false;
            }
            await using (stream)
            {
                context.Response.Clear();
                var original = context.RequestAborted;
                context.RequestAborted = lifetime.Token;
                try
                {
                    await Results.File(stream, offer.ContentType, offer.FileName, enableRangeProcessing: offer.Ranges,
                        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + offer.Sha256.ToLowerInvariant() + '"')).ExecuteAsync(context);
                }
                finally { context.RequestAborted = original; }
            }
            return true;
        }
        finally
        {
            await lifetime.CancelAsync();
            if (renew is not null) { try { await renew; } catch (OperationCanceledException) { } }
            using var cleanup = new CancellationTokenSource(LeaseReleaseTimeout);
            try { if (!offer.PreserveLease) { using var response = await client.DeleteAsync($"/api/site-services/storage/reads/{offer.LeaseId}", cleanup.Token); } }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { /* Bounded lease expiry recovers disconnected clients. */ }
        }
    }

    private static async Task RenewAsync(HttpClient client, Guid lease, CancellationToken ct)
    {
        using var response = await client.PostAsync($"/api/site-services/storage/reads/{lease}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task RenewLoopAsync(HttpClient client, Guid lease, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(LeaseRenewalInterval);
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await RenewAsync(client, lease, lifetime.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { await lifetime.CancelAsync(); }
    }
}
