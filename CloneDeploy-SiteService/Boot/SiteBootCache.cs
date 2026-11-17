using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

// Boot-only cache; future image/action storage does not change the listener's directory contract.
public sealed class SiteBootCache(SiteLocalStore store)
{
    private static readonly TimeSpan RetiredGenerationRetention = TimeSpan.FromDays(1);
    private const int MaximumBootFiles = 10_000;
    private const int MaximumReservations = 100_000;
    private const long MaximumBootFileBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumBootFileNameCharacters = 127;

    private readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> retired = new(StringComparer.OrdinalIgnoreCase);
    public string Root => Path.Combine(store.DirectoryPath, "Published", "Tftp");

    private string StagingRoot => Path.Combine(store.DirectoryPath, "Staging", "Tftp");

    public async Task<(SiteBootManifest Manifest, string Directory)> SynchronizeAsync(HttpClient http, CancellationToken ct)
    {
        var manifest = await http.GetFromJsonAsync<SiteBootManifest>("/api/site-services/boot/manifest", ct)
            ?? throw new InvalidDataException("Missing boot-file manifest.");
        Validate(manifest);
        var generation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest.Files))));
        var target = Path.Combine(Root, generation);
        SiteBootPaths.RejectLinks(target);
        Directory.CreateDirectory(Root);
        if (Directory.Exists(target))
        {
            await VerifyDirectoryAsync(target, manifest, ct);
            return (manifest, target);
        }
        var stage = Path.Combine(StagingRoot, "staging-" + Guid.NewGuid().ToString("N"));
        SiteBootPaths.RejectLinks(stage);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in manifest.Files)
            {
                var destination = SiteBootPaths.Resolve(stage, file.Path);
                // Reuse immutable verified files from the last downloaded generation when possible.
                var existing = Directory.EnumerateDirectories(Root).Where(x => Path.GetFileName(x).Length == SiteProxyProtocol.Sha256HexCharacters)
                    .Select(x => SiteBootPaths.Resolve(x, file.Path)).FirstOrDefault(x => File.Exists(x) && new FileInfo(x).Length == file.Length);
                if (existing is not null && await MatchesAsync(existing, file, ct))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(existing, destination);
                }
                else
                {
                    using var response = await http.GetAsync("/api/site-services/boot/file?path=" + Uri.EscapeDataString(file.Path), HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await VerifiedContentDownload.CopyAsync(source, destination, file.Length, file.Sha256, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); Directory.Move(stage, target);
            return (manifest, target);
        }
        finally { if (Directory.Exists(stage)) DeleteOwnedDirectory(stage); }
    }

    public static void Validate(SiteBootManifest manifest)
    {
        if (manifest.Files is null || manifest.Reservations is null || manifest.Files.Length is < 1 or > MaximumBootFiles || manifest.Reservations.Length > MaximumReservations)
            throw new InvalidDataException("Prepare boot files on the main server before enabling Site Service boot roles.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Files)
        {
            _ = SiteBootPaths.Resolve(Path.GetTempPath(), file.Path);
            if (!paths.Add(file.Path) || file.Length < 0 || file.Length > MaximumBootFileBytes || (total += file.Length) > MaximumManifestBytes ||
                file.Sha256 is null || file.Sha256.Length != SiteProxyProtocol.Sha256HexCharacters || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid boot-file manifest.");
        }
        foreach (var file in new[] { manifest.BiosBootFile, manifest.Efi32BootFile, manifest.Efi64BootFile, manifest.EfiArm64BootFile })
            if (string.IsNullOrWhiteSpace(file) || file.Length > MaximumBootFileNameCharacters) throw new InvalidDataException("Invalid PXE boot filename.");
        foreach (var reservation in manifest.Reservations)
            if (CloneDeploy_Server.Backend.Services.NativePxeReservations.Normalize(reservation.Mac) != reservation.Mac || reservation.BuildId == Guid.Empty || reservation.Architecture is not ("x64" or "arm64"))
                throw new InvalidDataException("Invalid native boot reservation.");
        if (manifest.KnownMacs is null || manifest.KnownMacs.Length > MaximumReservations ||
            manifest.KnownMacs.Any(mac => mac is null || CloneDeploy_Server.Backend.Services.NativePxeReservations.Normalize(mac) != mac))
            throw new InvalidDataException("Invalid native boot fallback exclusions.");
        foreach (var (target, folder) in new[] { (manifest.NativeDefault, "uefi"), (manifest.NativeArm64Default, "arm64") })
        {
            if (target is null) continue;
            var prefix = $"WinPe/{target.BuildId:N}";
            if (target.DefinitionId <= 0 || target.BuildId == Guid.Empty || !paths.Contains(prefix + "/ready.json") ||
                !paths.Contains(prefix + "/" + folder + "/bootmgfw.efi") || !paths.Contains(prefix + "/" + folder + "/BCD"))
                throw new InvalidDataException("Invalid native boot default.");
        }
    }
    private async Task VerifyDirectoryAsync(string directory, SiteBootManifest manifest, CancellationToken ct)
    {
        foreach (var file in manifest.Files)
            if (!await MatchesAsync(SiteBootPaths.Resolve(directory, file.Path), file, ct))
                throw new InvalidDataException("A cached boot file failed verification. Remove the damaged cache while the Site Service is stopped, then retry.");
    }
    private async Task<bool> MatchesAsync(string path, SiteBootFile file, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Length) return false;
        var key = path + "|" + info.LastWriteTimeUtc.Ticks + "|" + file.Sha256;
        if (verified.Contains(key)) return true;
        await using var input = File.OpenRead(path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        verified.Add(key); return true;
    }
    public void Cleanup(string active)
    {
        // Retain old generations for delayed native boot requests; never touch the active tree.
        retired.Remove(active);
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if (string.Equals(directory, active, StringComparison.OrdinalIgnoreCase)) continue;
            if (!retired.TryGetValue(directory, out var since)) retired[directory] = DateTime.UtcNow;
            else if (DateTime.UtcNow - since > RetiredGenerationRetention)
            {
                DeleteOwnedDirectory(directory);
                retired.Remove(directory);
                verified.RemoveWhere(key => key.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
    private void DeleteOwnedDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid cache cleanup path.");
        SiteBootPaths.RejectLinks(full);
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)) SiteBootPaths.RejectLinks(entry);
        Directory.Delete(full, true);
    }
}
