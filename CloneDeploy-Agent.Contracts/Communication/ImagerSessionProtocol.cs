using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

public static class ImagerSessionProtocol
{
    public const string HandshakePath = "/api/imaging/secure/handshake";
    public const string RequestPath = "/api/imaging/secure/request";
    public const int MaximumWireBytes = 40 * 1024 * 1024;
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    public const int LifetimeSeconds = 24 * 60 * 60;
    public static readonly string[] Headers = ["Authorization", "Content-Type", "Range", "If-Match", "If-Range",
        "X-CloneDeploy-Capture", "X-Imager-OU", SiteDirectDownload.CapabilityHeader, SiteDirectDownload.BypassHeader];
    public sealed record Hello(string PublicKey, string Nonce);
    public sealed record Welcome(string Id, string PublicKey, string Nonce, long Expires);
    public sealed record Packet(string Id, long Sequence, byte[] Data);
    public sealed record Request(string Method, string Path, Dictionary<string, string[]> Headers, byte[] Body);
    public sealed record Response(int Status, Dictionary<string, string[]> Headers, byte[] Body);
    public sealed record Keys(byte[] Request, byte[] Response);

    public static Keys Derive(ECDiffieHellman local, string peer, Hello hello, Welcome welcome)
    {
        using var remote = ECDiffieHellman.Create();
        var bytes = Convert.FromBase64String(peer);
        remote.ImportSubjectPublicKeyInfo(bytes, out var used);
        if (used != bytes.Length || remote.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("Invalid Imager session key.");
        var secret = local.DeriveRawSecretAgreement(remote.PublicKey);
        try
        {
            var salt = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "CloneDeploy/imager-session/v1", hello, welcome }));
            return new(HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "request"u8.ToArray()),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "response"u8.ToArray()));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public static Packet Seal<T>(byte[] key, string id, long sequence, string direction, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (plain.Length > MaximumWireBytes - 1024) throw new InvalidDataException("Imager session message too large.");
            var data = new byte[plain.Length + 16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(Nonce(sequence), plain, data.AsSpan(16), data.AsSpan(0, 16), Associated(id, sequence, direction));
            return new(id, sequence, data);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static T Open<T>(byte[] key, Packet packet, string direction)
    {
        if (packet.Sequence <= 0 || packet.Id is not { Length: 64 } || packet.Data is not { Length: >= 16 and <= MaximumWireBytes })
            throw new CryptographicException("Invalid Imager session packet.");
        var plain = new byte[packet.Data.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Nonce(packet.Sequence), packet.Data.AsSpan(16), packet.Data.AsSpan(0, 16), plain, Associated(packet.Id, packet.Sequence, direction));
            return JsonSerializer.Deserialize<T>(plain) ?? throw new CryptographicException("Empty Imager session message.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Nonce(long sequence)
    {
        if (sequence <= 0) throw new CryptographicException("Invalid sequence.");
        var nonce = new byte[12]; BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence); return nonce;
    }
    private static byte[] Associated(string id, long sequence, string direction) =>
        Encoding.UTF8.GetBytes($"CloneDeploy/imager-session/v1\n{id}\n{sequence}\n{direction}");

    public static bool IsArtifact(string method, string path) => method == "GET" &&
        (path == "/api/imaging/deployment/image" || path == "/api/imaging/deployment/ffu" ||
         path.StartsWith("/api/imaging/deployment/drivers/", StringComparison.Ordinal) && Guid.TryParse(path["/api/imaging/deployment/drivers/".Length..], out _));
}
