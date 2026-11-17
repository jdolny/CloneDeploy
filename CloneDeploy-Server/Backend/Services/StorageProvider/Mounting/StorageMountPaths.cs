using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages the local paths used to access configured storage locations.
/// </summary>
public sealed class StorageMountPaths(IOptions<StorageMountOptions> options, IWebHostEnvironment environment)
{
    public string RootPath { get; } = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);

    public string ProviderPath(int id)
    {
        if (id <= 0) throw new InvalidOperationException("Invalid storage provider ID.");
        return Path.Combine(RootPath, $"provider-{id}");
    }

    private string OwnershipPath(int id) => Path.Combine(RootPath, $".provider-{id}.json");

    public string ValidateTarget(StorageProvider provider)
    {
        var result = ValidateTargetInput(provider);
        return result.Path ?? throw new InvalidOperationException(result.Error);
    }

    public (string? Path, string? Error) ValidateTargetInput(StorageProvider provider)
    {
        if (provider.LinuxMountPath is { } linux && (!linux.StartsWith('/') || linux == "/" || linux.Contains('\\') ||
            linux.Any(char.IsControl) || linux.Split('/').Any(x => x is "." or "..")))
            return (null, "Use an absolute Linux mount path without relative segments.");
        if (provider.Type == StorageProviderType.Smb)
        {
            var result = ValidateSiteTarget(provider);
            if (result.Error is not null) return result;
            if (provider.SiteServiceId is null && OperatingSystem.IsLinux())
            {
                if (string.IsNullOrWhiteSpace(provider.LinuxMountPath)) return (null, "Configure an OS-managed Linux mount path for SMB storage.");
                if (IsWithin(provider.LinuxMountPath, RootPath) || IsWithin(RootPath, provider.LinuxMountPath))
                    return (null, "The storage target and mount root cannot contain one another.");
                var directories = new ServerDataPaths(environment);
                if (new[] { directories.Published, directories.Staging, directories.Temp }.Any(path =>
                        IsWithin(provider.LinuxMountPath, path) || IsWithin(path, provider.LinuxMountPath)))
                    return (null, "Storage providers cannot overlap the server's published, staging or temporary directories.");
            }
            return result;
        }
        // A remote path belongs to the site's OS, not the web server's OS.
        if (provider.SiteServiceId.HasValue) return ValidateSiteTarget(provider);
        var target = provider.Path?.Trim();
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return (null, "Use an absolute local path or a UNC share path.");
        if (target.StartsWith(@"\\?\") || target.StartsWith(@"\\.\")) return (null, "Device paths are not supported for storage providers.");
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "Use a valid absolute storage path.");
        }

        if (!System.Enum.IsDefined(provider.Type)) return (null, "Unsupported storage provider type.");
        var unc = target.StartsWith(@"\\");
        if (provider.Type == StorageProviderType.Smb && (!unc || target.TrimStart('\\').Split('\\').Length < 2))
            return (null, @"SMB storage requires a UNC path such as \\server\share\folder.");
        if (provider.Type == StorageProviderType.Local && unc) return (null, "Use the SMB type for a network share.");
        if (IsWithin(target, RootPath) || IsWithin(RootPath, target))
            return (null, "The storage target and mount root cannot contain one another.");
        var local = new ServerDataPaths(environment);
        if (new[] { local.Published, local.Staging, local.Temp }.Any(path => IsWithin(target, path) || IsWithin(path, target)))
            return (null, "Storage providers cannot overlap the server's published, staging or temporary directories.");
        return (target, null);
    }

    public static (string? Path, string? Error) ValidateWinPeSmbPath(string path)
    {
        var result = ValidateSiteTarget(new StorageProvider { Type = StorageProviderType.Smb, Path = path });
        if (result.Path is { } normalized && (normalized.Length > 2048 ||
            normalized[2..].Split('\\').Any(x => x.IndexOfAny([':', '*', '?', '"']) >= 0)))
            return (null, "Use a WinPE UNC path of at most 2048 characters without wildcards or invalid path characters.");
        return result;
    }

    private static (string? Path, string? Error) ValidateSiteTarget(StorageProvider provider)
    {
        var target = provider.Path?.Trim();
        if (string.IsNullOrEmpty(target) || target.Any(char.IsControl) || target.StartsWith(@"\\?\") || target.StartsWith(@"\\.\"))
            return (null, "Enter an absolute path on the site service host.");
        var unc = target.StartsWith(@"\\");
        var windows = target.Length >= 3 && char.IsAsciiLetter(target[0]) && target[1] == ':' && target[2] == '\\';
        var unix = target.StartsWith('/') && !target.Contains('\\');
        if (provider.Type == StorageProviderType.Smb)
        {
            if (!unc || target[2..].Split('\\').Length < 2 || target[2..].Split('\\').Take(2).Any(string.IsNullOrWhiteSpace))
                return (null, @"SMB storage requires a UNC path such as \\server\share\folder.");
        }
        else if (provider.Type != StorageProviderType.Local || (!windows && !unix))
            return (null, "Use an absolute local path on the site host, or choose SMB for a UNC path.");
        if (target.Split(['/', '\\']).Any(x => x is "." or "..") || (!unix && target.Contains('/')))
            return (null, "Use a storage path without relative segments or mixed separators.");
        var normalized = target.TrimEnd('/', '\\');
        if (normalized.Length == 0) normalized = "/";
        else if (windows && normalized.Length == 2) normalized += "\\";
        return (normalized, null);
    }

    public void EnsureLink(int id, string target)
    {
        if (RootPath.StartsWith(@"\\")) throw new InvalidOperationException("The mount root must be on local storage.");
        Directory.CreateDirectory(RootPath);
        if (new DirectoryInfo(RootPath).LinkTarget is not null) throw new InvalidOperationException("The mount root must be a real local directory.");
        var path = ProviderPath(id);
        var existing = new DirectoryInfo(path).LinkTarget;
        if (existing is not null && !SamePath(existing, target))
        {
            if (!File.Exists(OwnershipPath(id)) || !SamePath(ReadOwner(id), existing))
                throw new InvalidOperationException("The provider folder contains a link not owned by this application. It was left intact.");
            Directory.Delete(path, recursive: false); // Unlink only. Never recurse into provider contents.
            existing = null;
        }

        if (existing is null)
        {
            if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("The provider mount path is occupied by a real file or folder. It was left intact.");
            Directory.CreateSymbolicLink(path, target);
        }

        var temporary = OwnershipPath(id) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(target));
        File.Move(temporary, OwnershipPath(id), overwrite: true);
    }

    public void RemoveOwnedLink(int id)
    {
        if (!File.Exists(OwnershipPath(id))) return;
        var path = ProviderPath(id);
        var link = new DirectoryInfo(path).LinkTarget;
        if (link is not null)
        {
            if (!SamePath(link, ReadOwner(id))) throw new InvalidOperationException("The provider link was changed outside the application. It was left intact.");
            Directory.Delete(path, recursive: false);
        }
        else if (Directory.Exists(path) || File.Exists(path))
            throw new InvalidOperationException("A real folder occupies the provider mount path. It was left intact.");

        File.Delete(OwnershipPath(id));
    }

    public IEnumerable<int> OwnedProviderIds()
    {
        if (!Directory.Exists(RootPath)) return [];
        return Directory.EnumerateFiles(RootPath, ".provider-*.json", SearchOption.TopDirectoryOnly)
            .Select(x => Path.GetFileNameWithoutExtension(x)[10..]).Select(x => int.TryParse(x, out var id) ? id : 0).Where(x => x > 0).ToArray();
    }

    private string ReadOwner(int id) => JsonSerializer.Deserialize<string>(File.ReadAllText(OwnershipPath(id))) ?? "";
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root) => SamePath(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
