using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.WinPe;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Stages verified upstream files, then replaces only application-managed boot files.</summary>
public sealed class PxeBootFileManager(TftpRootDirectory root, IHostEnvironment environment,
    IHttpClientFactory clients, ILogger<PxeBootFileManager> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateDirectory = new ServerDataPaths(environment).PxePreparation;

    public async Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await read(); }
        finally { _gate.Release(); }
    }

    public PxeBootFilesDto? Read()
    {
        var path = Path.Combine(_stateDirectory, "installed.json");
        BuildStorage.RejectLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<PxeBootFilesDto>(File.ReadAllText(path)) : null;
    }

    public static string Autoexec(string serverUrl)
    {
        var url = ServerUrlSettingsStore.NormalizeBootUrl(serverUrl) ?? throw new InvalidDataException("Enter the boot server URL.");
        return "#!ipxe\ndhcp || goto failed\nchain --replace " + url +
               "api/boot?arch=${buildarch:uristring}&platform=${platform:uristring}&manufacturer=${manufacturer:uristring}&model=${product:uristring}&serial=${serial:uristring}&uuid=${uuid:uristring} || goto failed\n" +
               ":failed\necho Unable to load the CloneDeploy boot script.\nprompt Press any key to return.\nexit\n";
    }

    public async Task<PxeBootFilesDto> PrepareAsync(string serverUrl, CancellationToken ct)
    {
        var script = Autoexec(serverUrl);
        var url = ServerUrlSettingsStore.NormalizeBootUrl(serverUrl)!;
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("PXE file preparation is already running.");
        string? stage = null;
        try
        {
            root.EnsureExists();
            BuildStorage.RejectLinks(_stateDirectory);
            Directory.CreateDirectory(_stateDirectory);
            // Separate processes must not install over each other either.
            await using var ownership = new FileStream(Path.Combine(_stateDirectory, "prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stage = Path.Combine(_stateDirectory, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var http = clients.CreateClient("PxeBootFiles");
            var ipxe = await LatestAsync(http, "ipxe", "ipxeboot.tar.gz", timeout.Token);
            var wimboot = await LatestAsync(http, "wimboot", "wimboot", timeout.Token);
            var armWimboot = await LatestAsync(http, "wimboot", "wimboot.arm64", timeout.Token);
            if (armWimboot.Version != wimboot.Version) throw new InvalidDataException("The wimboot release changed during preparation. Retry.");
            var archive = await DownloadAssetAsync(http, ipxe, 32 * 1024 * 1024, timeout.Token);
            var wim = await DownloadAssetAsync(http, wimboot, 8 * 1024 * 1024, timeout.Token);
            var armWim = await DownloadAssetAsync(http, armWimboot, 8 * 1024 * 1024, timeout.Token);
            using var compressed = new MemoryStream(archive, writable: false);
            var files = await PxeBootArchive.ReadAsync(compressed, timeout.Token);
            files.Add("wimboot", wim);
            files.Add("wimboot.arm64", armWim);
            files["autoexec.ipxe"] = Encoding.ASCII.GetBytes(script);
            foreach (var loader in files.Keys.Where(x => x.EndsWith("/ipxe.efi", StringComparison.OrdinalIgnoreCase)).ToArray())
                files[loader[..loader.LastIndexOf('/')] + "/autoexec.ipxe"] = Encoding.ASCII.GetBytes(script);
            // Keep upstream notices beside the runtime downloads. These binaries are not bundled with the app.
            foreach (var notice in new[] { "COPYING", "COPYING.GPLv2", "COPYING.UBDL" })
                files[$"ipxeboot/licenses/{notice}"] = await GetAsync(http, new Uri($"https://raw.githubusercontent.com/ipxe/ipxe/{Uri.EscapeDataString(ipxe.Version)}/{notice}"), 1024 * 1024, timeout.Token);
            files["wimboot.COPYING"] = await GetAsync(http, new Uri($"https://raw.githubusercontent.com/ipxe/wimboot/{Uri.EscapeDataString(wimboot.Version)}/LICENSE.txt"), 1024 * 1024, timeout.Token);
            files["ipxeboot/licenses/SOURCES.txt"] = Encoding.UTF8.GetBytes($"iPXE source: https://github.com/ipxe/ipxe/tree/{ipxe.Version}\nwimboot source: https://github.com/ipxe/wimboot/tree/{wimboot.Version}\n");
            var result = new PxeBootFilesDto(ipxe.Version, wimboot.Version, url, DateTime.UtcNow, ipxe.Sha256, wimboot.Sha256) { Arm64WimbootSha256 = armWimboot.Sha256 };
            foreach (var file in files)
            {
                var staged = Path.Combine(stage, "new", file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, file.Value, timeout.Token);
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(result), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            // After staging succeeds, finish commit or rollback even if the UI disconnects.
            try { Publish(stage, files.Keys.OrderBy(x => x.EndsWith("autoexec.ipxe", StringComparison.Ordinal) ? 1 : 0).ToArray()); }
            catch (AggregateException)
            {
                logger.LogError("PXE rollback was incomplete; recovery files retained at {Stage}", stage);
                stage = null; // Retain backups for recovery if the filesystem also prevents rollback.
                throw;
            }
            logger.LogInformation("Prepared PXE files: iPXE {Ipxe}, wimboot {Wimboot}", result.IpxeVersion, result.WimbootVersion);
            return result;
        }
        finally
        {
            if (stage is not null)
            {
                try
                {
                    var full = Path.GetFullPath(stage);
                    if (full.StartsWith(Path.GetFullPath(_stateDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    { BuildStorage.RejectLinks(full); Directory.Delete(full, true); }
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove PXE staging directory"); }
            }
            _gate.Release();
        }
    }

    private void Publish(string stage, string[] files)
    {
        var completed = new List<(string Destination, string? Backup)>();
        try
        {
            foreach (var name in files)
            {
                PxeBootArchive.ValidatePath(name);
                var destination = Path.GetFullPath(Path.Combine(root.RootPath, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid boot destination.");
                BuildStorage.RejectLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Replace(Path.Combine(stage, "new", name.Replace('/', Path.DirectorySeparatorChar)), destination);
            }
            Replace(Path.Combine(stage, "manifest.json"), Path.Combine(_stateDirectory, "installed.json"));
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var file in completed.AsEnumerable().Reverse())
            {
                try
                {
                    BuildStorage.RejectLinks(file.Destination);
                    if (file.Backup is null) File.Delete(file.Destination);
                    else File.Replace(file.Backup, file.Destination, null);
                }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            if (errors.Count > 1) throw new AggregateException("PXE file rollback was incomplete. Recovery files were retained.", errors);
            throw;
        }
        void Replace(string source, string destination)
        {
            BuildStorage.RejectLinks(destination);
            string? backup = null;
            if (File.Exists(destination))
            {
                backup = Path.Combine(stage, "backup-" + completed.Count);
                File.Replace(source, destination, backup);
            }
            else File.Move(source, destination);
            completed.Add((destination, backup));
        }
    }

    private sealed record Asset(string Version, Uri Url, long Size, string Sha256);

    private static async Task<Asset> LatestAsync(HttpClient http, string repository, string filename, CancellationToken ct)
    {
        using var release = JsonDocument.Parse(await GetAsync(http, new Uri($"https://api.github.com/repos/ipxe/{repository}/releases/latest"), 1024 * 1024, ct));
        var json = release.RootElement;
        if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("No stable release is available.");
        var version = json.GetProperty("tag_name").GetString()!;
        if (version.Length is < 1 or > 100 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_')) throw new InvalidDataException("Invalid release tag.");
        var asset = json.GetProperty("assets").EnumerateArray().Single(x => x.GetProperty("name").GetString() == filename);
        var url = asset.GetProperty("browser_download_url").GetString()!;
        if (!url.StartsWith($"https://github.com/ipxe/{repository}/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release download URL.");
        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:") || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("The release asset has no usable SHA-256 digest.");
        return new(version, new Uri(url), asset.GetProperty("size").GetInt64(), digest[7..].ToLowerInvariant());
    }

    private static async Task<byte[]> DownloadAssetAsync(HttpClient http, Asset asset, int limit, CancellationToken ct)
    {
        if (asset.Size <= 0 || asset.Size > limit) throw new InvalidDataException("Unexpected release asset size.");
        var bytes = await GetAsync(http, asset.Url, limit, ct);
        if (bytes.LongLength != asset.Size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release download size or SHA-256 verification failed.");
        return bytes;
    }

    private static async Task<byte[]> GetAsync(HttpClient http, Uri url, int limit, CancellationToken ct)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                url.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "raw.githubusercontent.com"))
                throw new InvalidDataException("Unexpected download host or protocol.");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            { url = new Uri(url, response.Headers.Location ?? throw new InvalidDataException("Missing redirect location.")); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Download exceeds the allowed size.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Download exceeds the allowed size.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("Too many release download redirects.");
    }
}
