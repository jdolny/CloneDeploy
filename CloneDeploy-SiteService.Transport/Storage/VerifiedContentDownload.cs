using System.Security.Cryptography;

namespace CloneDeploy.SiteServices;

// Also suitable for future content replicas: publish only a fully verified download.
public static class VerifiedContentDownload
{
    private const int TransferBufferBytes = 128 * 1024;

    public static async Task CopyAsync(Stream source, string destination, long length, string sha256, CancellationToken ct)
    {
        if (length < 0 || sha256.Length != SiteProxyProtocol.Sha256HexCharacters || !sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid content metadata.");
        SiteBootPaths.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, TransferBufferBytes, true))
            {
                var buffer = new byte[TransferBufferBytes]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > length) throw new InvalidDataException("Content exceeds its declared length.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (total != length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Content checksum or length mismatch.");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
