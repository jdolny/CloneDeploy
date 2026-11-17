using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

/// <summary>Versioned JWS (ES256) profile for main-server messages, independent of TLS termination.</summary>
public static class ServerSignatures
{
    public const string NonceHeader = "X-CloneDeploy-Signing-Nonce";
    public const string SignatureHeader = "X-CloneDeploy-Server-Signature";
    // Standard code-signing purpose plus an exact CA-authenticated role name. Neither TLS
    // leaves nor other future code-signing certificates are authorized for this protocol.
    public const string SigningPurpose = "1.3.6.1.5.5.7.3.3";
    public const string SigningSubject = "CN=CloneDeploy server message signing";
    public const int MaximumControlBytes = 16 * 1024 * 1024;
    public const int MaximumTokenCharacters = 32 * 1024;
    public static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool ValidNonce(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    public static readonly string[] BoundHeaders = ["Authorization", "Content-Type", "X-Task-Lease", "X-CloneDeploy-Admin-Token",
        "X-CloneDeploy-Windows-Sid", "X-CloneDeploy-Windows-Session", "X-CloneDeploy-ImagePrep-Uninstall", "X-CloneDeploy-Capture"];
    public static string RequestHash(string bodyHash, Func<string, string[]> headers) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new { Body = bodyHash, Headers = BoundHeaders.Select(headers).ToArray() }));
    public static string Context(string nonce, string method, string pathAndQuery, string bodyHash, string certificateHash) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new[] { "CloneDeploy/server/v1", nonce, method, pathAndQuery, bodyHash, certificateHash }));

    public sealed record HttpResponse(int Status, Dictionary<string, string[]> Headers, string BodyHash);
    public sealed record SocketFrame(long Sequence, int Type, bool End, byte[] Data);
    private sealed record Message<T>(string Context, long Issued, long Expires, T Value);

    public static string Sign<T>(X509Certificate2 certificate, string context, T value, DateTimeOffset now)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "clonedeploy-server-v1", x5c = new[] { Convert.ToBase64String(certificate.RawData) } }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Message<T>(context, now.ToUnixTimeSeconds(), now.AddMinutes(2).ToUnixTimeSeconds(), value)));
        using var key = certificate.GetECDsaPrivateKey() ?? throw new CryptographicException("Missing server signing key.");
        var input = header + "." + payload;
        return input + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static T Verify<T>(string token, X509Certificate2 root, string context, DateTimeOffset now, int maximumCharacters = MaximumTokenCharacters)
    {
        try
        {
            if (token.Length > maximumCharacters) throw new CryptographicException("Oversized server signature.");
            var parts = token.Split('.');
            if (parts.Length != 3 || parts[0].Length > 8192) throw new CryptographicException("Invalid server signature.");
            using var header = JsonDocument.Parse(Decode(parts[0]));
            var h = header.RootElement;
            if (h.EnumerateObject().Count() != 3 || h.GetProperty("alg").GetString() != "ES256" ||
                h.GetProperty("typ").GetString() != "clonedeploy-server-v1" || h.GetProperty("x5c").GetArrayLength() != 1)
                throw new CryptographicException("Unsupported server signature.");
            using var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(h.GetProperty("x5c")[0].GetString()!));
            ValidateCertificate(leaf, root, now);
            using var key = leaf.GetECDsaPublicKey() ?? throw new CryptographicException("Invalid server signing key.");
            var parameters = key.ExportParameters(false);
            if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("Server signature verification failed.");
            var message = JsonSerializer.Deserialize<Message<T>>(Decode(parts[1])) ?? throw new CryptographicException("Missing signed message.");
            var seconds = now.ToUnixTimeSeconds();
            if (message.Context != context || message.Issued > seconds + 30 || message.Expires < seconds ||
                message.Expires < message.Issued || message.Expires - message.Issued > 120)
                throw new CryptographicException("Server message is expired or belongs to another request/device.");
            return message.Value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new CryptographicException("Invalid signed server message.", ex); }
    }

    public static void ValidateCertificate(X509Certificate2 leaf, X509Certificate2 root, DateTimeOffset now)
    {
        if (!leaf.SubjectName.RawData.AsSpan().SequenceEqual(new X500DistinguishedName(SigningSubject).RawData) ||
            leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != false ||
            leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault()?.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
            leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault()?.EnhancedKeyUsages.Cast<Oid>().Select(x => x.Value).SequenceEqual(new[] { SigningPurpose }) != true)
            throw new CryptographicException("Certificate is not authorized to sign server messages.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(SigningPurpose));
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (!chain.Build(leaf) || chain.ChainElements.Count != 2)
            throw new CryptographicException("Untrusted server signing certificate: " + string.Join(", ", chain.ChainStatus.Select(x => x.Status)));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}
