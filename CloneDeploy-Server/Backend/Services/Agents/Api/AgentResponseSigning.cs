using System.Security.Cryptography;
using CloneDeploy.SiteServices;
using CloneDeploy_Agent.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace CloneDeploy_Server.Backend.Services;

public static class AgentResponseSigning
{
    public static IApplicationBuilder UseAgentResponseSigning(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var imager = context.Request.Headers.ContainsKey(ImagerSignatures.ProtocolHeader) &&
            (context.Request.Path.StartsWithSegments("/api/imaging") || context.Request.Path == "/api/site-services/storage/abandon");
        var bootstrap = imager && context.Request.Headers[ImagerSignatures.ProtocolHeader].ToString() == ImagerSignatures.BootstrapVersion &&
            HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path == "/api/imaging/client/win-x64.json" || context.Request.Path == "/api/imaging/client/win-arm64.json");
        if (imager && (context.Request.Headers[ImagerSignatures.ProtocolHeader].Count != 1 ||
            !bootstrap && context.Request.Headers[ImagerSignatures.ProtocolHeader].ToString() != ImagerSignatures.Version))
        { context.Response.StatusCode = 400; return; }
        if (!(imager || context.Request.Path.StartsWithSegments("/api/agent") ||
              context.Request.Path.StartsWithSegments("/api/imaging/prepared-capture/uploads") ||
              context.Request.Path == "/api/site-services/storage/abandon") || !context.Request.Headers.ContainsKey(ServerSignatures.NonceHeader))
        { await next(context); return; }
        var nonce = context.Request.Headers[ServerSignatures.NonceHeader];
        if (nonce.Count != 1 || !ServerSignatures.ValidNonce(nonce.ToString()))
        { context.Response.StatusCode = 400; return; }
        var ct = context.RequestAborted;
        var certificate = imager ? null : await TrustedSiteProxy.GetAgentCertificateAsync(context);
        var limit = Math.Min(ServerSignatures.MaximumControlBytes,
            context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? ServerSignatures.MaximumControlBytes);
        // Apply endpoint limits before reading; MVC would normally apply these later.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = Math.Min(size.MaxRequestBodySize ?? limit, limit);
        var path = context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        var requestHeaders = (imager ? ImagerSignatures.BoundHeaders : ServerSignatures.BoundHeaders)
            .ToDictionary(name => name, name => context.Request.Headers[name].Select(v => v ?? "").ToArray());
        // A proxy can upgrade an HTTP/1 GET WebSocket to HTTP/2 extended CONNECT.
        // Bind the logical WebSocket operation, independent of hop transport version.
        string Binding(string bodyHash) => bootstrap ? ImagerSignatures.BootstrapContext(nonce.ToString(), path) : imager
            ? ImagerSignatures.Context(nonce.ToString(), context.Request.Method, path, bodyHash, name => requestHeaders[name])
            : ServerSignatures.Context(nonce.ToString(), context.WebSockets.IsWebSocketRequest ? "GET" : context.Request.Method, path,
            ServerSignatures.RequestHash(bodyHash, name => requestHeaders[name]), certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "");
        var signer = context.RequestServices.GetRequiredService<ServerMessageSigner>();
        if (context.WebSockets.IsWebSocketRequest && context.Features.Get<IHttpWebSocketFeature>() is { } webSockets)
        {
            context.Features.Set<IHttpWebSocketFeature>(new SignedSockets(webSockets, signer, Binding(ServerSignatures.Hash([])), context));
            try { await next(context); }
            finally { context.Features.Set(webSockets); }
            return;
        }
        var original = context.Response.Body;
        var originalRequest = context.Request.Body;
        using var requestBody = new HashingRequest(originalRequest, limit);
        context.Request.Body = requestBody;
        using var buffer = new LimitedBuffer();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            // Hash as MVC reads, preserving its normal request-limit filters and avoiding an
            // extra buffered copy of uploads. Include any unread suffix in the binding too.
            await requestBody.CopyToAsync(Stream.Null, ct);
            var headers = context.Response.Headers.Where(x => !ExcludedHeader(x.Key))
                .ToDictionary(x => x.Key, x => x.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);
            var signature = await signer.SignAsync(Binding(requestBody.Digest()), new ServerSignatures.HttpResponse(context.Response.StatusCode, headers,
                ServerSignatures.Hash(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))), ct);
            if (signature.Length > ServerSignatures.MaximumTokenCharacters) throw new IOException("Agent response headers exceed the signature limit.");
            context.Response.Headers[ServerSignatures.SignatureHeader] = signature;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = buffer.Length;
            context.Response.Body = original;
            buffer.Position = 0;
            if (buffer.Length != 0) await buffer.CopyToAsync(original, ct);
        }
        finally { context.Response.Body = original; context.Request.Body = originalRequest; }
    });

    private static bool ExcludedHeader(string name) => name.Equals(ServerSignatures.SignatureHeader, StringComparison.OrdinalIgnoreCase) ||
        new[] { "Connection", "Transfer-Encoding", "Content-Length", "Server", "Date", "Keep-Alive", "Upgrade", "Trailer" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private sealed class SignedSockets(IHttpWebSocketFeature inner, ServerMessageSigner signer, string binding, HttpContext request) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;
        public async Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            var verified = request.Features.Get<AgentRequestVerification.Verified>();
            return new ServerSignedWebSocket(await inner.AcceptAsync(context), (frame, ct) => signer.SignAsync(binding, frame, ct),
                verified is null ? null : token =>
                {
                    using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(verified.Certificate);
                    return AgentSignatures.VerifyFrame(certificate, verified.SocketBinding, token);
                });
        }
    }

    private sealed class LimitedBuffer : MemoryStream
    {
        private void Check(int count)
        { if (Position + count > ServerSignatures.MaximumControlBytes) throw new IOException("Signed agent control response exceeds its limit."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    }

    private sealed class HashingRequest(Stream inner, long limit) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long length;
        public string Digest() => Convert.ToHexString(hash.GetHashAndReset());
        private void Append(ReadOnlySpan<byte> bytes)
        {
            length += bytes.Length;
            if (length > limit) throw new BadHttpRequestException("Agent request exceeds its size limit.", StatusCodes.Status413PayloadTooLarge);
            hash.AppendData(bytes);
        }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = inner.Read(buffer, offset, count); Append(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await inner.ReadAsync(buffer, ct); Append(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }
}
