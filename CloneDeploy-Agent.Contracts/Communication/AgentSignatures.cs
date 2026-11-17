using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

/// <summary>Agent proof of possession, independent of the proxy's TLS identity.</summary>
public static class AgentSignatures
{
    public const string Header = "X-CloneDeploy-Agent-Signature";
    public const int LifetimeSeconds = 120;
    public sealed record Proof(string Nonce, long Issued, string Binding);
    private static readonly string[] Headers = ServerSignatures.BoundHeaders.Concat(new[]
    {
        ServerSignatures.NonceHeader, "Range", "If-Range", "If-Match", "If-None-Match",
        "If-Modified-Since", "If-Unmodified-Since", "Content-Encoding"
    }).ToArray();

    public static string Binding(string method, string path, string bodyHash, string certificateHash,
        Func<string, string[]> headers, bool socket = false) => ServerSignatures.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { Purpose = "CloneDeploy/agent-request/v1", Method = method, Path = path, Body = bodyHash,
            Certificate = certificateHash, Socket = socket, Headers = Headers.Select(headers).ToArray() }));

    public static string SignRequest(X509Certificate2 certificate, string binding, DateTimeOffset now) =>
        Sign(certificate, "request", new Proof(ServerSignatures.Nonce(), now.ToUnixTimeSeconds(), binding));

    public static Proof VerifyRequest(X509Certificate2 certificate, string token, string binding, DateTimeOffset now)
    {
        var proof = Verify<Proof>(certificate, "request", token, 8192);
        if (proof.Nonce is null || !ServerSignatures.ValidNonce(proof.Nonce) || proof.Binding != binding ||
            proof.Issued > now.ToUnixTimeSeconds() + 30 || proof.Issued < now.ToUnixTimeSeconds() - LifetimeSeconds)
            throw new CryptographicException("Invalid or expired agent request proof.");
        return proof;
    }

    public static string SignFrame(X509Certificate2 certificate, string binding, ServerSignatures.SocketFrame frame) =>
        Sign(certificate, "socket/" + binding, frame);
    public static ServerSignatures.SocketFrame VerifyFrame(X509Certificate2 certificate, string binding, string token) =>
        Verify<ServerSignatures.SocketFrame>(certificate, "socket/" + binding, token, ServerSignedWebSocket.MaximumTokenBytes);

    private static string Sign<T>(X509Certificate2 certificate, string purpose, T value)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
        using var key = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Missing agent private key.");
        var signature = key.SignData(Encoding.UTF8.GetBytes("CloneDeploy/agent/v1/" + purpose + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return payload + "." + Convert.ToBase64String(signature);
    }

    private static T Verify<T>(X509Certificate2 certificate, string purpose, string token, int maximum)
    {
        try
        {
            if (token.Length > maximum) throw new CryptographicException("Oversized agent proof.");
            var parts = token.Split('.');
            if (parts.Length != 2) throw new CryptographicException("Malformed agent proof.");
            using var key = certificate.GetRSAPublicKey() ?? throw new CryptographicException("Invalid agent public key.");
            if (!key.VerifyData(Encoding.UTF8.GetBytes("CloneDeploy/agent/v1/" + purpose + "." + parts[0]),
                    Convert.FromBase64String(parts[1]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Invalid agent signature.");
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(parts[0])) ?? throw new CryptographicException("Missing agent proof.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        { throw new CryptographicException("Malformed agent proof.", ex); }
    }
}
