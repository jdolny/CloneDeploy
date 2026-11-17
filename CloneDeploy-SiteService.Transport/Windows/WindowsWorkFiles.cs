using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace CloneDeploy.SiteServices;

public static class WindowsWorkFiles
{
    private const int MaximumFileNameCharacters = 255;
    private const int TransferBufferBytes = 1024 * 1024;

    public const long MaximumBytes = 40L * 1024 * 1024 * 1024;
    public static WindowsWorkerRoles Role(string kind) => kind switch
    {
        "WinPe" or "WinPeSetup" => WindowsWorkerRoles.WinPe,
        "Driver" or "Catalog" => WindowsWorkerRoles.Drivers,
        "MsiVersion" or "MsiArchitecture" => WindowsWorkerRoles.AgentPackages,
        _ => throw new InvalidDataException("Unknown Windows work type.")
    };
    public static string Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > MaximumFileNameCharacters || name.Any(char.IsControl) ||
            name.IndexOfAny(['/', '\\', '<', '>', ':', '"', '|', '?', '*']) >= 0 || name.EndsWith('.') ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Invalid work file name.");
        return name;
    }
    public static void Validate(WindowsWorkFile file)
    {
        Name(file.Name);
        if (file.Length is < 0 or > MaximumBytes || file.Sha256 is not { Length: SiteProxyProtocol.Sha256HexCharacters } || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid work file metadata.");
    }
    public static async Task<WindowsWorkFile> DescribeAsync(string path, string name, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        var file = new WindowsWorkFile(Name(name), input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)));
        Validate(file); return file;
    }
    public static async Task CopyAsync(Stream input, string path, WindowsWorkFile expected, CancellationToken ct)
    {
        Validate(expected);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, TransferBufferBytes, true))
            {
                var buffer = new byte[TransferBufferBytes]; long length = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct); if (count == 0) break;
                    length += count; if (length > expected.Length) throw new InvalidDataException("Work file exceeds its assigned size.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (length != expected.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Work file verification failed.");
                await output.FlushAsync(ct);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
