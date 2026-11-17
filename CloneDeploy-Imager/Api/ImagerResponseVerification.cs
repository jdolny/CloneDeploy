using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Imager.Api;

/// <summary>Authenticates main-server control replies before any application code consumes them.</summary>
internal sealed class ImagerResponseVerification : DelegatingHandler
{
    private readonly byte[] trustedRoot;
    private static readonly HttpRequestOptionsKey<bool> HashBoundDownload = new("CloneDeploy.Imager.HashBoundDownload");

    public ImagerResponseVerification(HttpMessageHandler transport, string caPath) : base(transport)
    {
        using var root = X509CertificateLoader.LoadCertificateFromFile(caPath);
        trustedRoot = root.RawData;
        if (transport is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; }
    }

    // Call only for image/driver bytes covered by an already authenticated deployment plan.
    // This never exempts JSON, credentials, capture read-back, or other control responses.
    public static void MarkDownload(HttpRequestMessage request)
    {
        var path = request.RequestUri!.IsAbsoluteUri ? request.RequestUri.AbsolutePath : request.RequestUri.OriginalString;
        if (request.Method != HttpMethod.Get || !(path.EndsWith("api/imaging/deployment/image", StringComparison.Ordinal) ||
            path.EndsWith("api/imaging/deployment/ffu", StringComparison.Ordinal) ||
            path.Contains("api/imaging/deployment/drivers/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Only deployment artifacts can use hash-bound downloads.");
        request.Options.Set(HashBoundDownload, true);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // The owning secure-session handler authenticates these ciphertext replies with its negotiated key.
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith(ImagerSessionProtocol.RequestPath, StringComparison.Ordinal))
            return await base.SendAsync(request, ct);
        request.Headers.Remove(ImagerSignatures.ProtocolHeader);
        request.Headers.Remove(ServerSignatures.NonceHeader);
        if (request.Options.TryGetValue(HashBoundDownload, out var download) && download)
            return await base.SendAsync(request, ct);

        var nonce = ServerSignatures.Nonce();
        request.Headers.Add(ImagerSignatures.ProtocolHeader, ImagerSignatures.Version);
        request.Headers.Add(ServerSignatures.NonceHeader, nonce);
        var body = Array.Empty<byte>();
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(ServerSignatures.MaximumControlBytes, ct);
            body = await request.Content.ReadAsByteArrayAsync(ct);
        }
        var binding = ImagerSignatures.Context(nonce, request.Method.Method, request.RequestUri!.PathAndQuery, ServerSignatures.Hash(body),
            name => request.Headers.TryGetValues(name, out var values) ? values.ToArray() :
                request.Content?.Headers.TryGetValues(name, out var contentValues) == true ? contentValues.ToArray() : []);
        var response = await base.SendAsync(request, ct);
        try
        {
            if (!response.Headers.TryGetValues(ServerSignatures.SignatureHeader, out var values))
                throw new CryptographicException("Unsigned Imager response. Upgrade the main server and rebuild trusted WinPE media.");
            var signatures = values.ToArray();
            if (signatures.Length != 1) throw new CryptographicException("Multiple Imager response signatures.");
            using var root = X509CertificateLoader.LoadCertificate(trustedRoot);
            var signed = ServerSignatures.Verify<ServerSignatures.HttpResponse>(signatures[0], root, binding, DateTimeOffset.UtcNow);
            if (signed.Status != (int)response.StatusCode) throw new CryptographicException("Imager response status was modified.");
            await response.Content.LoadIntoBufferAsync(ServerSignatures.MaximumControlBytes, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (signed.BodyHash != ServerSignatures.Hash(bytes)) throw new CryptographicException("Imager response content was modified.");
            response.Content.Dispose();
            response.Content = new ByteArrayContent(bytes);
            response.Headers.Clear();
            foreach (var header in signed.Headers)
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value)) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
