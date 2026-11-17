using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloneDeploy.SiteService;

public sealed class EnrollmentKeyGate
{
    private const int ProvisioningKeyHexCharacters = 64;

    private byte[]? expectedHash;

    public void Configure(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) { Volatile.Write(ref expectedHash, null); return; }
        if (hash.Length != CloneDeploy.SiteServices.SiteProxyProtocol.Sha256HexCharacters || !hash.All(Uri.IsHexDigit))
        {
            // An invalid update must not retain a previously accepted key.
            Volatile.Write(ref expectedHash, null);
            throw new InvalidDataException("Invalid agent provisioning key hash.");
        }
        Volatile.Write(ref expectedHash, Convert.FromHexString(hash));
    }

    public bool Accepts(ReadOnlyMemory<byte> body)
    {
        var hash = Volatile.Read(ref expectedHash);
        if (hash is null) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? key = null;
            var count = 0;
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "ProvisioningKey", StringComparison.OrdinalIgnoreCase)) continue;
                if (++count != 1 || property.Value.ValueKind != JsonValueKind.String) return false;
                key = property.Value.GetString();
            }
            return key is { Length: ProvisioningKeyHexCharacters } && key.All(Uri.IsHexDigit) &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), hash);
        }
        catch (JsonException) { return false; }
    }
}
