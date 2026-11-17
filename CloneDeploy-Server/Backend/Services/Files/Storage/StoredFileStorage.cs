using System.Text.RegularExpressions;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services.Files;

/// <summary>
/// Locates stored files and checks file names, download addresses, and file contents.
/// </summary>
public sealed class StoredFileStorage(IOptions<UploadOptions> options, IWebHostEnvironment environment, StorageContentStorage? providers = null)
{
    // Serialize metadata/file publication with edits, deletion, and re-download requests.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public long MaxFileSize => options.Value.MaxFileSize;

    public async Task<int?> SelectProviderAsync(CancellationToken ct = default)
    {
        return await SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose.DeviceManagement, ct);
    }

    public async Task<int?> SelectProviderAsync(CloneDeploy_Server.Backend.Enum.StoragePurpose purpose, CancellationToken ct = default)
        => await (providers ?? throw new InvalidOperationException("Storage providers are not configured.")).SelectProviderAsync(purpose, ct);

    public Task EnsureReadyAsync(StoredFile file, CancellationToken ct = default) =>
        file.StorageProviderId is { } id
            ? (providers ?? throw new InvalidOperationException("Storage providers are not configured.")).EnsureReadyAsync(id, ct)
            : throw new InvalidOperationException("The file has no storage provider.");

    public string NamedPath(StoredFile file, string? name = null) => file.StorageProviderId is { } id
        ? Path.Combine((providers ?? throw new InvalidOperationException("Storage providers are not configured.")).GetRoot(id),
            "actions", file.DirectoryGuid.ToString("N"), ValidateName(name ?? file.Name))
        : throw new InvalidOperationException("The file has no storage provider.");

    public long? NamedSize(StoredFile file)
    {
        try
        {
            var info = new FileInfo(NamedPath(file));
            return info.Exists ? info.Length : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public string NewTemporaryPath()
    {
        var directory = Path.Combine(Path.GetFullPath(options.Value.TempStoragePath, environment.ContentRootPath), "pending");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.partial");
    }

    // Only the external-download worker owns bare GUID partials. Upload sessions
    // use upload-<guid>.partial and have their own cleanup and lifetime tracking.
    public IEnumerable<string> AbandonedDownloadPaths(DateTime cutoffUtc)
    {
        var directory = Path.Combine(Path.GetFullPath(options.Value.TempStoragePath, environment.ContentRootPath), "pending");
        if (!Directory.Exists(directory)) yield break;
        CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.partial", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (File.GetLastWriteTimeUtc(path) < cutoffUtc) yield return path;
        }
    }

    public static string ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(char.IsControl) ||
            name.IndexOfAny(['/', '\\', '<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            name.EndsWith('.') || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Enter a valid filename without folders.");
        return name;
    }

    public static string ValidateExternalName(string? name)
    {
        name = ValidateName(name);
        if (name.LastIndexOf('.') <= 0 || string.IsNullOrWhiteSpace(Path.GetExtension(name).TrimStart('.')))
            throw new InvalidOperationException("Include a file extension, for example setup.exe or package.zip.");
        return name;
    }

    public static string? SuggestExternalName(string? url)
    {
        try
        {
            var uri = new Uri(ValidateUrl(url));
            var name = ValidateExternalName(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()));
            // Web endpoints do not identify the payload's filename.
            return Path.GetExtension(name).ToLowerInvariant() is ".php" or ".asp" or ".aspx" or ".jsp" or ".cgi" or ".html" or ".htm"
                ? null : name;
        }
        catch (InvalidOperationException) { return null; }
        catch (UriFormatException) { return null; }
    }

    public static string ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Enter an HTTP or HTTPS URL without embedded credentials.");
        return uri.AbsoluteUri;
    }

    public static string? ValidateHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        if (!Regex.IsMatch(hash.Trim(), "^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException("SHA-256 must contain 64 hexadecimal characters.");
        return hash.Trim().ToLowerInvariant();
    }
}
