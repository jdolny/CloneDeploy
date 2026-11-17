using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public static class AgentRequestVerification
{
    public sealed record Verified(string SocketBinding, byte[] Certificate);

    public static async Task<bool> VerifyAsync(HttpContext context, X509Certificate2 certificate)
    {
        var tokens = context.Request.Headers[AgentSignatures.Header];
        if (tokens.Count != 1 || tokens[0] is not { Length: > 0 and <= 8192 } token) return false;
        var ct = context.RequestAborted;
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var limit = Math.Min(ServerSignatures.MaximumControlBytes,
            context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? ServerSignatures.MaximumControlBytes);
        if (context.Request.ContentLength > limit) throw new BadHttpRequestException("Agent request exceeds its size limit.", 413);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = Math.Min(size.MaxRequestBodySize ?? limit, limit);
        var socket = context.WebSockets.IsWebSocketRequest;
        if (socket && (context.Request.Headers[ServerSignatures.NonceHeader].Count != 1 ||
            !ServerSignatures.ValidNonce(context.Request.Headers[ServerSignatures.NonceHeader].ToString()))) return false;
        var hash = ServerSignatures.Hash([]);
        if (!socket)
        {
            // Verify the entire body BEFORE controllers can perform side effects. Buffering
            // spills to temporary storage, with the endpoint limit still enforced.
            context.Request.EnableBuffering(64 * 1024, limit + 1);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[8192];
            long length = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, limit + 1 - length)), ct)) > 0)
            {
                length += read;
                if (length > limit) throw new BadHttpRequestException("Agent request exceeds its size limit.", 413);
                digest.AppendData(bytes.AsSpan(0, read));
            }
            hash = Convert.ToHexString(digest.GetHashAndReset());
            context.Request.Body.Position = 0;
            // MVC applies the same endpoint limit later. The transport is now read-only,
            // but the buffered body can still be checked against any smaller MVC limit.
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BufferedSize(length, limit));
        }
        var path = context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        var binding = AgentSignatures.Binding(socket ? "GET" : context.Request.Method, path, hash,
            certificate.GetCertHashString(HashAlgorithmName.SHA256), name => context.Request.Headers[name].Select(v => v ?? "").ToArray(), socket);
        AgentSignatures.Proof proof;
        try { proof = AgentSignatures.VerifyRequest(certificate, token, binding, clock.GetUtcNow()); }
        catch (CryptographicException) { return false; }
        await using var db = await context.RequestServices.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = DateTimeOffset.FromUnixTimeSeconds(proof.Issued).AddSeconds(AgentSignatures.LifetimeSeconds + 1).UtcDateTime;
        var id = ServerSignatures.Hash(Encoding.UTF8.GetBytes(certificate.GetCertHashString(HashAlgorithmName.SHA256) + proof.Nonce));
        // Atomic across processes and durable across restarts. Cleanup never removes a
        // request while its timestamp can still pass validation.
        await db.Set<AgentRequestNonce>().Where(x => x.ExpiresAtUtc < now).ExecuteDeleteAsync(ct);
        if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AgentRequestNonces\" (\"Id\", \"ExpiresAtUtc\") VALUES ({id}, {expires}) ON CONFLICT DO NOTHING", ct) != 1)
            return false;
        context.Features.Set(new Verified(ServerSignatures.Hash(Encoding.UTF8.GetBytes(token)), certificate.RawData));
        return true;
    }

    private sealed class BufferedSize(long length, long limit) : IHttpMaxRequestBodySizeFeature
    {
        private long? maximum = limit;
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize
        {
            get => maximum;
            set
            {
                if (value is { } size && length > size) throw new BadHttpRequestException("Agent request exceeds its size limit.", 413);
                maximum = value;
            }
        }
    }
}
