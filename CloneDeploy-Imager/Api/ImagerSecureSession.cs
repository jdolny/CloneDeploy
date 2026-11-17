using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Imager.Api;

internal sealed class ImagerSecureSession(HttpMessageHandler transport, string caPath) : DelegatingHandler(new ImagerResponseVerification(transport, caPath))
{
    private readonly SemaphoreSlim handshake = new(1);
    private ImagerSessionProtocol.Welcome? session;
    private ImagerSessionProtocol.Keys? keys;
    private long sequence;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var api = uri.AbsolutePath.IndexOf("/api/", StringComparison.Ordinal);
        if (api < 0) throw new InvalidOperationException("Invalid imaging API path.");
        var prefix = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..api];
        await handshake.WaitAsync(ct);
        try
        {
            if (session is null)
            {
                using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var hello = new ImagerSessionProtocol.Hello(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), ServerSignatures.Nonce());
                using var start = new HttpRequestMessage(HttpMethod.Post, prefix + ImagerSessionProtocol.HandshakePath) { Content = JsonContent.Create(hello) };
                using var reply = await base.SendAsync(start, ct); // Dedicated main signature verified before key derivation or credentials.
                reply.EnsureSuccessStatusCode();
                var welcome = await reply.Content.ReadFromJsonAsync<ImagerSessionProtocol.Welcome>(ct) ?? throw new CryptographicException("Missing Imager handshake.");
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!ServerSignatures.ValidNonce(welcome.Id) || welcome.Nonce != hello.Nonce || welcome.Expires <= now || welcome.Expires > now + ImagerSessionProtocol.LifetimeSeconds + 30)
                    throw new CryptographicException("Invalid Imager handshake.");
                keys = ImagerSessionProtocol.Derive(key, welcome.PublicKey, hello, welcome);
                session = welcome;
            }
        }
        finally { handshake.Release(); }
        if (session.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new CryptographicException("Imager session expired. Restart Imager.");
        var number = Interlocked.Increment(ref sequence);
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync(ImagerSessionProtocol.MaximumBodyBytes, ct);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = ImagerSessionProtocol.Headers.Select(name => (name, values: request.Headers.TryGetValues(name, out var values) ? values.ToArray() :
            request.Content?.Headers.TryGetValues(name, out values) == true ? values.ToArray() : []))
            .Where(x => x.values.Length != 0).ToDictionary(x => x.name, x => x.values);
        var encrypted = ImagerSessionProtocol.Seal(keys!.Request, session.Id, number, "request",
            new ImagerSessionProtocol.Request(request.Method.Method, uri.PathAndQuery, headers, body));
        using var envelope = new HttpRequestMessage(HttpMethod.Post, prefix + ImagerSessionProtocol.RequestPath) { Content = JsonContent.Create(encrypted) };
        var response = await base.SendAsync(envelope, ct);
        if (ImagerSessionProtocol.IsArtifact(request.Method.Method, uri.AbsolutePath[api..])) return response;
        try
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new CryptographicException("Imager session was rejected or lost. Restart Imager; no request was automatically retried.");
            await response.Content.LoadIntoBufferAsync(ImagerSessionProtocol.MaximumWireBytes, ct);
            var packet = await response.Content.ReadFromJsonAsync<ImagerSessionProtocol.Packet>(ct) ?? throw new CryptographicException("Missing encrypted reply.");
            if (packet.Id != session.Id || packet.Sequence != number) throw new CryptographicException("Replayed or unrelated Imager reply.");
            var plain = ImagerSessionProtocol.Open<ImagerSessionProtocol.Response>(keys.Response, packet, "response");
            response.StatusCode = (HttpStatusCode)plain.Status;
            response.Headers.Clear(); response.Content.Dispose(); response.Content = new ByteArrayContent(plain.Body);
            foreach (var header in plain.Headers)
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && keys is not null) { CryptographicOperations.ZeroMemory(keys.Request); CryptographicOperations.ZeroMemory(keys.Response); }
        base.Dispose(disposing);
    }
}
