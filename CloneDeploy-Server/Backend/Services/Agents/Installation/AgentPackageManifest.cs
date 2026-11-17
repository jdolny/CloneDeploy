using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.Backend.Services;

// Build-time metadata shipped with the immutable MSI, like the macOS PKG manifest.
public sealed record AgentPackageManifest(int SchemaVersion, string Version, string Architecture, string UpgradeCode, long Size, string Sha256)
{
    public static async Task<AgentPackageManifest> ReadAsync(Stream source, string architecture, CancellationToken ct)
    {
        var buffer = new byte[4097];
        var used = 0;
        while (used < buffer.Length)
        {
            var count = await source.ReadAsync(buffer.AsMemory(used), ct);
            if (count == 0) break;
            used += count;
        }
        if (used > 4096) throw new InvalidDataException("Agent package manifest is too large.");
        var value = JsonSerializer.Deserialize<AgentPackageManifest>(buffer.AsSpan(0, used))
            ?? throw new InvalidDataException("Missing agent package metadata.");
        if (value.SchemaVersion != 1 || value.Architecture != WindowsArchitecture.Require(architecture) ||
            !string.Equals(value.UpgradeCode, WindowsArchitecture.UpgradeCode(architecture), StringComparison.OrdinalIgnoreCase) ||
            value.Size <= 0 || value.Sha256 is not { Length: 64 } || !value.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(value.Version))
            throw new InvalidDataException("Invalid or mismatched agent package metadata.");
        return value with { Version = AgentUpdateOffer.ParseVersion(value.Version).ToString() };
    }

    public async Task VerifyAsync(Stream package, CancellationToken ct)
    {
        if (package.Length != Size) throw new InvalidDataException("Agent MSI size does not match its manifest. Publish the matching MSI and manifest.");
        package.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(package, ct));
        if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Agent MSI checksum does not match its manifest. Publish the matching MSI and manifest.");
        package.Position = 0;
    }

    public static async Task<AgentPackageManifest> ValidateFileAsync(string path, string architecture, CancellationToken ct)
    {
        await using var metadata = File.OpenRead(path + ".json");
        var manifest = await ReadAsync(metadata, architecture, ct);
        await using var package = File.OpenRead(path);
        await manifest.VerifyAsync(package, ct);
        return manifest;
    }
}
