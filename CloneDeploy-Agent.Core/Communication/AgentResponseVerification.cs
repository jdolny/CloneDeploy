using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Every ordinary API response must be signed, on direct and proxied connections alike.</summary>
public sealed class AgentResponseVerification : DelegatingHandler
{
    private readonly HttpClientHandler transport;
    private readonly AgentOptions options;
    public AgentResponseVerification(HttpClientHandler inner, AgentOptions options) : base(inner)
    {
        transport = inner;
        this.options = options;
        // Agent sessions use explicit tokens; do not process unsigned cookies inside the
        // transport before response verification. Browser clients retain their normal cookies.
        inner.UseCookies = false;
    }
    private static readonly HttpRequestOptionsKey<bool> HashBoundDownload = new("CloneDeploy.HashBoundDownload");

    // Only for content whose SHA-256 came from an already verified control response. The caller
    // must verify the complete downloaded file before installing, executing or publishing it.
    public static async Task<HttpResponseMessage> DownloadAsync(HttpClient client, string path, string expectedSha256, CancellationToken ct)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid signed download hash.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Options.Set(HashBoundDownload, true);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var hashBound = request.Options.TryGetValue(HashBoundDownload, out var download) && download;
        var nonce = ServerSignatures.Nonce();
        request.Headers.Remove(ServerSignatures.NonceHeader);
        if (!hashBound) request.Headers.Add(ServerSignatures.NonceHeader, nonce);
        var body = Array.Empty<byte>();
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(ServerSignatures.MaximumControlBytes, ct);
            body = await request.Content.ReadAsByteArrayAsync(ct);
        }
        var clientCertificate = transport.ClientCertificates.OfType<X509Certificate2>().SingleOrDefault();
        if (clientCertificate is not null)
        {
            var requestBinding = AgentSignatures.Binding(request.Method.Method, request.RequestUri!.PathAndQuery,
                ServerSignatures.Hash(body), clientCertificate.GetCertHashString(HashAlgorithmName.SHA256),
                name => request.Headers.TryGetValues(name, out var values) ? values.ToArray() :
                    request.Content?.Headers.TryGetValues(name, out var contentValues) == true ? contentValues.ToArray() : []);
            request.Headers.Remove(AgentSignatures.Header);
            request.Headers.Add(AgentSignatures.Header, AgentSignatures.SignRequest(clientCertificate, requestBinding, DateTimeOffset.UtcNow));
        }
        // Downloads still prove the requesting device; their responses are verified against
        // hashes supplied by signed control messages rather than buffering large files.
        if (hashBound)
        {
            return await base.SendAsync(request, ct);
        }
        var binding = ServerSignatures.Context(nonce, request.Method.Method, request.RequestUri!.PathAndQuery,
            ServerSignatures.RequestHash(ServerSignatures.Hash(body), name => request.Headers.TryGetValues(name, out var values) ? values.ToArray() :
                request.Content?.Headers.TryGetValues(name, out var contentValues) == true ? contentValues.ToArray() : []),
            clientCertificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "");
        var response = await base.SendAsync(request, ct);
        try
        {
            if (!response.Headers.TryGetValues(ServerSignatures.SignatureHeader, out var signatures))
                throw new CryptographicException("The server returned an unsigned agent response. Upgrade the main server before using this agent.");
            var tokens = signatures.ToArray();
            if (tokens.Length != 1) throw new CryptographicException("The server returned multiple response signatures.");
            using var root = X509CertificateLoader.LoadCertificateFromFile(options.ServerCaCertificatePath!);
            var signed = ServerSignatures.Verify<ServerSignatures.HttpResponse>(tokens[0], root, binding, DateTimeOffset.UtcNow);
            if (signed.Status != (int)response.StatusCode) throw new CryptographicException("Server response status was modified.");
            await response.Content.LoadIntoBufferAsync(ServerSignatures.MaximumControlBytes, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (ServerSignatures.Hash(bytes) != signed.BodyHash) throw new CryptographicException("Server response content was modified.");
            response.Content.Dispose();
            response.Content = new ByteArrayContent(bytes);
            response.Headers.Clear();
            foreach (var header in signed.Headers)
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    public static async Task<WebSocket> ConnectSocketAsync(Uri uri, AgentOptions options, X509Certificate2 certificate, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.ClientCertificates.Add(certificate);
            AgentServerTrust.Configure(socket.Options, options);
            var nonce = ServerSignatures.Nonce();
            socket.Options.SetRequestHeader(ServerSignatures.NonceHeader, nonce);
            var requestBinding = AgentSignatures.Binding("GET", uri.PathAndQuery, ServerSignatures.Hash([]),
                certificate.GetCertHashString(HashAlgorithmName.SHA256), name => name == ServerSignatures.NonceHeader ? [nonce] : [], true);
            var proof = AgentSignatures.SignRequest(certificate, requestBinding, DateTimeOffset.UtcNow);
            socket.Options.SetRequestHeader(AgentSignatures.Header, proof);
            var binding = ServerSignatures.Context(nonce, "GET", uri.PathAndQuery, ServerSignatures.RequestHash(ServerSignatures.Hash([]), _ => []), certificate.GetCertHashString(HashAlgorithmName.SHA256));
            // Snapshot trust for this connection; do not accept a certificate supplied by the proxy.
            using var ca = X509CertificateLoader.LoadCertificateFromFile(options.ServerCaCertificatePath!);
            var root = ca.RawData;
            await socket.ConnectAsync(uri, ct);
            return new ServerSignedWebSocket(socket,
                (frame, _) => Task.FromResult(AgentSignatures.SignFrame(certificate, ServerSignatures.Hash(System.Text.Encoding.UTF8.GetBytes(proof)), frame)), token =>
            {
                using var trusted = X509CertificateLoader.LoadCertificate(root);
                return ServerSignatures.Verify<ServerSignatures.SocketFrame>(token, trusted, binding, DateTimeOffset.UtcNow, ServerSignedWebSocket.MaximumTokenBytes);
            });
        }
        catch { socket.Dispose(); throw; }
    }
}
