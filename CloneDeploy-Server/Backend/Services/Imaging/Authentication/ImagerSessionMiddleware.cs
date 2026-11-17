using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace CloneDeploy_Server.Backend.Services;

public static class ImagerSessionMiddleware
{
    private sealed record Authenticated(int Length);
    public static bool IsAuthenticated(HttpContext context) => context.Features.Get<Authenticated>() is not null;
    public static IApplicationBuilder UseImagerSessions(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path != ImagerSessionProtocol.RequestPath) { await next(context); return; }
        if (!context.Request.IsHttps || !HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 400; return; }
        var ct = context.RequestAborted;
        ImagerSessionProtocol.Packet packet;
        ImagerSessions.Session session;
        ImagerSessionProtocol.Request request;
        try
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = ImagerSessionProtocol.MaximumWireBytes;
            using var wire = new BoundedMemory(ImagerSessionProtocol.MaximumWireBytes);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await context.Request.Body.CopyToAsync(wire, deadline.Token);
            packet = JsonSerializer.Deserialize<ImagerSessionProtocol.Packet>(wire.GetBuffer().AsSpan(0, (int)wire.Length), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new CryptographicException();
            (session, request) = context.RequestServices.GetRequiredService<ImagerSessions>().Open(packet);
            if (request.Body is null || request.Body.Length > ImagerSessionProtocol.MaximumBodyBytes || request.Headers is null || request.Headers.Count > 10 ||
                request.Path is not { Length: > 0 and < 8192 } || request.Path.Contains('#') || request.Path.Contains('\\') || request.Path.Any(char.IsControl)) throw new CryptographicException();
            var parts = request.Path.Split('?', 2);
            var path = PathString.FromUriComponent(parts[0]);
            if (!path.StartsWithSegments(context.Request.PathBase, out var relative)) throw new CryptographicException();
            var releaseTicket = relative == "/api/site-services/storage/abandon" && HttpMethods.IsPost(request.Method);
            if (!releaseTicket && (!relative.StartsWithSegments("/api/imaging") || relative.StartsWithSegments("/api/imaging/secure") ||
                relative.StartsWithSegments("/api/imaging/client") || !SiteProxyRoutes.Allows(relative, request.Method))) throw new CryptographicException();
            foreach (var header in request.Headers)
                if (!ImagerSessionProtocol.Headers.Contains(header.Key, StringComparer.OrdinalIgnoreCase) || header.Value is null ||
                    header.Value.Length > 4 || header.Value.Any(x => x is null || x.Length > 32768 || x.Any(char.IsControl))) throw new CryptographicException();
            context.Request.Method = request.Method;
            context.Request.Path = relative;
            context.Request.QueryString = parts.Length == 2 ? new QueryString("?" + parts[1]) : QueryString.Empty;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or IOException or InvalidOperationException)
        { context.Response.StatusCode = 401; return; }

        // Retain only the transport's proxy identity headers. Application inputs come exclusively from authenticated plaintext.
        foreach (var name in ImagerSessionProtocol.Headers.Append(ImagerSignatures.ProtocolHeader).Append(ServerSignatures.NonceHeader)) context.Request.Headers.Remove(name);
        context.Request.Headers.Remove("Cookie");
        foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value;
        context.Request.Headers.Remove("Transfer-Encoding");
        context.Request.ContentLength = request.Body.Length;
        context.Features.Set(new Authenticated(request.Body.Length));
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BufferedSize(request.Body.Length));
        using var input = new MemoryStream(request.Body, writable: false);
        context.Request.Body = input;
        var original = context.Response.Body;
        var artifact = ImagerSessionProtocol.IsArtifact(request.Method, context.Request.Path.Value!);
        if (artifact) { await next(context); return; } // Artifact bytes retain the existing client verification settings; control replies remain authenticated.
        using var output = new BoundedMemory(ImagerSessionProtocol.MaximumBodyBytes);
        context.Response.Body = output;
        try
        {
            await next(context);
            var headers = context.Response.Headers.Where(x => !new[] { "Content-Length", "Transfer-Encoding", "Connection", "Set-Cookie" }.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value.Select(v => v ?? "").ToArray());
            var reply = ImagerSessionProtocol.Seal(session.Keys.Response, packet.Id, packet.Sequence, "response",
                new ImagerSessionProtocol.Response(context.Response.StatusCode, headers, output.ToArray()));
            context.Response.Headers.Clear();
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Body = original;
            await JsonSerializer.SerializeAsync(original, reply, cancellationToken: ct);
        }
        finally { context.Response.Body = original; CryptographicOperations.ZeroMemory(request.Body); }
    });

    // After routing/signing, before authorization/controllers. Legacy callers cannot bypass the authenticated channel.
    public static IApplicationBuilder UseImagerSessionAdmission(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Features.Get<Authenticated>() is { } authenticated)
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
            if (limit is { } maximum && authenticated.Length > maximum) { context.Response.StatusCode = 413; return; }
        }
        else if (context.Request.Path.StartsWithSegments("/api/imaging") &&
            !context.Request.Path.StartsWithSegments("/api/imaging/client") && context.Request.Path != ImagerSessionProtocol.HandshakePath)
        {
            // Installed agents can finalize prepared captures only with their existing, independently verified request proofs.
            if (!context.Request.Path.StartsWithSegments("/api/imaging/prepared-capture/uploads") ||
                !(await context.AuthenticateAsync(AgentCertificateAuthentication.SchemeName)).Succeeded)
            { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
        }
        await next(context);
    });
    private sealed class BufferedSize(long length) : IHttpMaxRequestBodySizeFeature
    {
        private long? maximum = ImagerSessionProtocol.MaximumBodyBytes;
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get => maximum; set { if (value is { } limit && length > limit) throw new BadHttpRequestException("Imager request too large.", 413); maximum = value; } }
    }
    private sealed class BoundedMemory(int maximum) : MemoryStream
    {
        private void Check(int count) { if (Position + count > maximum) throw new IOException("Imager message too large."); }
        public override void Write(byte[] b, int o, int c) { Check(c); base.Write(b, o, c); }
        public override void Write(ReadOnlySpan<byte> b) { Check(b.Length); base.Write(b); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) { Check(b.Length); return base.WriteAsync(b, ct); }
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) { Check(c); return base.WriteAsync(b, o, c, ct); }
    }
}
