using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

// Only follows a storage offer from the authenticated main API, never general redirects.
// The separate connection receives no agent certificate, cookies, or main-server bearer token.
public sealed class SiteDownloadClient(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaximumEncodedOfferCharacters = 16384;
    private const int MaximumTicketCharacters = 8192;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get) return await base.SendAsync(request, ct);
        request.Headers.TryAddWithoutValidation(SiteDirectDownload.CapabilityHeader, "1");
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NoContent || !response.Headers.TryGetValues(SiteDirectDownload.OfferHeader, out var headers)) return response;
        string? ticket = null;
        try
        {
            var encoded = headers.Single();
            if (encoded.Length > MaximumEncodedOfferCharacters) throw new InvalidDataException("Invalid storage handoff.");
            var offer = JsonSerializer.Deserialize<SiteDirectDownload>(Convert.FromBase64String(encoded)) ?? throw new InvalidDataException("Missing storage handoff.");
            ticket = offer.Ticket;
            if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo != "" ||
                url.AbsolutePath != SiteDirectDownload.Path || url.Query != "" || url.Fragment != "" ||
                !IPAddress.TryParse(offer.Address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                offer.CertificateSha256 is not { Length: 64 } || !offer.CertificateSha256.All(Uri.IsHexDigit) || offer.Ticket.Length is < 1 or > MaximumTicketCharacters)
                throw new InvalidDataException("Invalid storage handoff destination.");
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
            handler.ConnectCallback = async (connection, token) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(new IPEndPoint(ip, connection.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0 ||
                    !certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(offer.CertificateSha256, StringComparison.OrdinalIgnoreCase)) return false;
                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return leaf.NotBefore.ToUniversalTime() <= DateTime.UtcNow && leaf.NotAfter.ToUniversalTime() > DateTime.UtcNow;
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                using var download = new HttpRequestMessage(HttpMethod.Get, url);
                download.Headers.TryAddWithoutValidation(SiteDirectDownload.TicketHeader, offer.Ticket);
                foreach (var name in new[] { "Range", "If-Range", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since" })
                    if (request.Headers.TryGetValues(name, out var values)) download.Headers.TryAddWithoutValidation(name, values);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var result = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (result.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.NotModified or HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.PreconditionFailed)
                {
                    result.Content = new OwnedContent(result.Content, client);
                    response.Dispose();
                    return result;
                }
                result.Dispose();
            }
            catch { client.Dispose(); throw; }
            client.Dispose();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or JsonException or InvalidOperationException) { }
        finally { response.Dispose(); }
        if (ticket is { Length: > 0 and <= MaximumTicketCharacters })
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cleanup.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var abandon = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri!, "/api/site-services/storage/abandon"))
                    { Content = JsonContent.Create(new { Ticket = ticket, ClientAddress = "" }) };
                using var released = await base.SendAsync(abandon, cleanup.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or CryptographicException) { }
        }
        // Retry the original authorized request once, restricting selection to main storage.
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var option in request.Options) retry.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        retry.Headers.TryAddWithoutValidation(SiteDirectDownload.BypassHeader, "1");
        return await base.SendAsync(retry, ct);
    }

    private sealed class OwnedContent : HttpContent
    {
        private readonly HttpContent content;
        private readonly HttpClient owner;
        public OwnedContent(HttpContent content, HttpClient owner)
        {
            this.content = content; this.owner = owner;
            foreach (var header in content.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await content.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => content.CopyToAsync(stream, ct);
        protected override Task<Stream> CreateContentReadStreamAsync() => content.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => content.ReadAsStreamAsync(ct);
        protected override bool TryComputeLength(out long length) { length = content.Headers.ContentLength ?? -1; return length >= 0; }
        protected override void Dispose(bool disposing) { if (disposing) { content.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
    }
}
