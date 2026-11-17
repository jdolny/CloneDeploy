using System.Security.Cryptography;

namespace CloneDeploy_Server.Backend.Services;

public static class CaptureUploadChecksum
{
    public static async Task<string> ComputeAsync(Stream file, CancellationToken ct)
    {
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return hash.ToLowerInvariant();
    }
}
