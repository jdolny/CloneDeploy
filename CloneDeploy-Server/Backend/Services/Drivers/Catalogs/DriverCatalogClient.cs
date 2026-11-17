using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Vendor metadata only. Downloaded driver installers are never executed by this service.
/// <summary>
/// Retrieves lists of available driver packages from hardware vendors.
/// </summary>
public sealed class DriverCatalogClient(IHttpClientFactory clients, IHostEnvironment environment)
{
    public Func<byte[], CancellationToken, Task<string?>>? RemoteCatalog { get; init; }
    public const string DellCatalog = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";
    public const string HpCatalog = "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab";
    public const string HpWinPeCatalog = "https://ftp.hp.com/pub/caps-softpaq/cmit/HP_WinPE_DriverPack.html";
    public const string LenovoCatalog = "https://download.lenovo.com/cdrt/td/catalogv2.xml";
    public const string SurfaceIndex = "https://learn.microsoft.com/en-us/surface/manage-surface-driver-and-firmware-updates";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime Time, List<DriverCatalogPackageDto> Packages)> _cache = [];
    private List<DriverCatalogModelDto>? _surfaceModels;
    private DateTime _surfaceTime;

    public async Task<List<DriverCatalogModelDto>> GetModelsAsync(DriverVendor vendor, bool refresh = false, CancellationToken ct = default)
    {
        if (!System.Enum.IsDefined(vendor)) throw new InvalidOperationException("Choose a supported manufacturer.");
        if (vendor != DriverVendor.Microsoft)
        {
            var models = (await PackagesAsync(vendor, "", refresh, ct)).Select(x => new DriverCatalogModelDto(x.Model, x.Source)).Distinct().ToList();
            if (vendor == DriverVendor.HP) models.Add(new DriverCatalogModelDto("WinPE driver packs", HpWinPeCatalog));
            return models.OrderBy(x => x.Name).ToList();
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (refresh || _surfaceModels is null || DateTime.UtcNow - _surfaceTime > TimeSpan.FromHours(6))
            {
                var html = await TextAsync(SurfaceIndex, ct);
                _surfaceModels = ParseSurfaceModels(html);
                if (_surfaceModels.Count == 0) throw new InvalidOperationException("Microsoft's model index has changed or is unavailable. Use a direct download URL for now.");
                _surfaceTime = DateTime.UtcNow;
            }

            return _surfaceModels.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<DriverCatalogPackageDto>> GetPackagesAsync(DriverVendor vendor, string model, string source = "", bool refresh = false, CancellationToken ct = default)
    {
        if (vendor == DriverVendor.Microsoft)
        {
            var models = await GetModelsAsync(vendor, false, ct);
            if (!models.Any(x => x.Name == model && x.Source == source)) throw new InvalidOperationException("Select a model from the current Microsoft catalog.");
        }

        return (await PackagesAsync(vendor, source, refresh, ct)).Where(x => x.Model == model || vendor == DriverVendor.Microsoft)
            .Select(x => vendor == DriverVendor.Microsoft ? x with { Model = model } : x)
            .OrderByDescending(x => x.ReleasedAtUtc).ThenByDescending(x => Version.TryParse(x.Release, out var version) ? version : new Version(0, 0)).ThenByDescending(x => x.Release, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<DriverCatalogPackageDto>> PackagesAsync(DriverVendor vendor, string source, bool refresh, CancellationToken ct)
    {
        if (!System.Enum.IsDefined(vendor)) throw new InvalidOperationException("Choose a supported manufacturer.");
        var key = vendor + (vendor == DriverVendor.Microsoft || source == HpWinPeCatalog ? source : "");
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromHours(6)) return cached.Packages;
            List<DriverCatalogPackageDto> packages = vendor switch
            {
                DriverVendor.Dell => ParseDell(await CabinetXmlAsync(DellCatalog, ct)),
                DriverVendor.HP => source == HpWinPeCatalog ? ParseHpWinPe(await TextAsync(HpWinPeCatalog, ct)) : ParseHp(await CabinetXmlAsync(HpCatalog, ct)),
                DriverVendor.Lenovo => ParseLenovo(await TextAsync(LenovoCatalog, ct)),
                DriverVendor.Microsoft => ParseSurfacePackages(await TextAsync(ValidateMicrosoftSource(source), ct), source),
                _ => throw new InvalidOperationException("Unsupported catalog.")
            };
            packages = packages.Where(x => !string.IsNullOrWhiteSpace(x.Model) && IsVendorDownload(vendor, x.Url)).DistinctBy(x => x.Key).ToList();
            if (packages.Count == 0) throw new InvalidOperationException("The vendor returned no downloadable driver packs. Try again or add a direct URL.");
            _cache[key] = (DateTime.UtcNow, packages);
            return packages;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> BytesAsync(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        ct = deadline.Token;
        using var client = clients.CreateClient("DriverCatalogs");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        const int limit = 32 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Catalog exceeds the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidOperationException("Catalog exceeds the size limit.");
            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    private async Task<string> TextAsync(string url, CancellationToken ct) => Encoding.UTF8.GetString(await BytesAsync(url, ct));

    private async Task<string> CabinetXmlAsync(string url, CancellationToken ct)
    {
        var bytes = await BytesAsync(url, ct);
        if (RemoteCatalog is not null && await RemoteCatalog(bytes, ct) is { } remote) return remote;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Enable a Windows driver worker for Dell and HP catalog extraction.");
        var folder = Path.Combine(new ServerDataPaths(environment).EnsureTempDirectory(), "Catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cabinet = Path.Combine(folder, "catalog.cab");
            var xml = Path.Combine(folder, "catalog.xml");
            await File.WriteAllBytesAsync(cabinet, bytes, ct);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(cabinet);
            start.ArgumentList.Add(xml);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not open the driver catalog.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }

            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0 || !File.Exists(xml) || new FileInfo(xml).Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("Could not extract the vendor catalog.");
            return await File.ReadAllTextAsync(xml, ct);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static XDocument Xml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text.TrimStart('\uFEFF')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    private static IEnumerable<XElement> Desc(XContainer e, string name) => e.Descendants().Where(x => x.Name.LocalName == name);
    private static string Value(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
    private static string Attr(XElement e, string name) => e.Attribute(name)?.Value.Trim() ?? "";
    private static DateTime? Date(string text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    private static string? Hash(string text) => Regex.IsMatch(text, "^[a-fA-F0-9]{64}$") ? text.ToLowerInvariant() : null;
    private static long? Size(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0 ? size : null;
    private static string FileName(string url) => Uri.UnescapeDataString(new Uri(url).AbsolutePath.Split('/').Last());

    public static bool IsVendorDownload(DriverVendor vendor, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) return false;
        return vendor switch
        {
            DriverVendor.Dell => uri.Host is "downloads.dell.com" or "dl.dell.com",
            DriverVendor.HP => uri.Host is "ftp.hp.com" or "ftp.ext.hp.com" or "hpia.hpcloud.hp.com",
            DriverVendor.Lenovo => uri.Host == "download.lenovo.com",
            DriverVendor.Microsoft => uri.Host == "download.microsoft.com",
            _ => false
        };
    }

    public static List<DriverCatalogPackageDto> ParseDell(string text)
    {
        var result = new List<DriverCatalogPackageDto>();
        foreach (var p in Desc(Xml(text), "DriverPackage"))
        {
            var path = Attr(p, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            var url = new Uri(new Uri("https://downloads.dell.com/"), path).AbsoluteUri;
            var hash = p.Elements().FirstOrDefault(x => x.Name.LocalName == "Cryptography")?.Elements().FirstOrDefault(x => Attr(x, "algorithm") == "SHA256")?.Value.Trim();
            var models = Attr(p, "type").Equals("winpe", StringComparison.OrdinalIgnoreCase)
                ? new[] { "WinPE driver packs" }
                : Desc(p, "Model").Select(x => Attr(x, "name"));
            foreach (var model in models)
            foreach (var os in Desc(p, "OperatingSystem"))
                result.Add(new(DriverVendor.Dell, model, Value(os, "Display"), Attr(os, "osArch"), Attr(p, "dellVersion"),
                    Date(Attr(p, "dateTime")), FileName(url), url, Hash(hash ?? ""), DellCatalog) { DownloadSizeBytes = Size(Attr(p, "size")) });
        }

        return result;
    }

    public static List<DriverCatalogPackageDto> ParseHp(string text)
    {
        var xml = Xml(text);
        var packs = Desc(xml, "SoftPaq").GroupBy(x => Value(x, "Id")).ToDictionary(x => x.Key, x => x.First());
        var result = new List<DriverCatalogPackageDto>();
        foreach (var link in Desc(xml, "ProductOSDriverPack"))
        {
            if (!packs.TryGetValue(Value(link, "SoftPaqId"), out var p)) continue;
            var url = Value(p, "Url").Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) continue;
            result.Add(new(DriverVendor.HP, Value(link, "SystemName"), Value(link, "OSName"), Value(link, "Architecture"), Value(p, "Version"),
                Date(Value(p, "DateReleased")), FileName(url), url, Hash(Value(p, "SHA256")), HpCatalog) { DownloadSizeBytes = Size(Value(p, "Size")) });
        }

        return result;
    }

    public static List<DriverCatalogPackageDto> ParseHpWinPe(string html)
    {
        var result = new List<DriverCatalogPackageDto>();
        foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            var cells = Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2))
                .Select(x => WebUtility.HtmlDecode(Regex.Replace(x.Groups[1].Value, "<[^>]+>", "")).Trim()).ToList();
            if (cells.Count < 5 || !cells[0].StartsWith("WinPE", StringComparison.OrdinalIgnoreCase)) continue;
            var link = Regex.Match(row.Value, "href=[\"'](?<url>https://ftp(?:\\.ext)?\\.hp\\.com/[^\"']+\\.exe)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (!link.Success) continue;
            var url = WebUtility.HtmlDecode(link.Groups["url"].Value);
            result.Add(new(DriverVendor.HP, "WinPE driver packs", cells[0], "See package contents", cells[1], Date(cells[3]), FileName(url), url, null, HpWinPeCatalog));
        }

        return result;
    }

    public static List<DriverCatalogPackageDto> ParseLenovo(string text)
    {
        var result = new List<DriverCatalogPackageDto>();
        foreach (var model in Desc(Xml(text), "Model"))
        foreach (var p in model.Elements().Where(x => x.Name.LocalName == "SCCM"))
        {
            var url = p.Value.Trim().Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) continue;
            var file = FileName(url);
            var arch = file.Contains("arm", StringComparison.OrdinalIgnoreCase) ? "ARM64" : file.Contains("32", StringComparison.OrdinalIgnoreCase) ? "x86" : "x64";
            result.Add(new(DriverVendor.Lenovo, Attr(model, "name"), Attr(p, "os") + " " + Attr(p, "version"), arch, file,
                Date(Attr(p, "date")), file, url, Hash(Attr(p, "crc")), LenovoCatalog));
        }

        return result;
    }

    public static List<DriverCatalogModelDto> ParseSurfaceModels(string html)
    {
        var matches = Regex.Matches(html, "<a\\b[^>]*href=[\\\"'](?<url>https://www\\.microsoft\\.com/[^\\\"']*download/[^\\\"']*details[^\\\"']*)[\\\"'][^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        return matches.Select(m => new DriverCatalogModelDto(WebUtility.HtmlDecode(Regex.Replace(m.Groups["name"].Value, "<[^>]+>", "")).Trim(),
                WebUtility.HtmlDecode(m.Groups["url"].Value))).Where(x => x.Name.StartsWith("Surface", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Source).OrderBy(x => x.Name).ToList();
    }

    private static string ValidateMicrosoftSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "www.microsoft.com" || !uri.AbsolutePath.Contains("/download/"))
            throw new InvalidOperationException("Invalid Microsoft model source.");
        return source;
    }

    private static Dictionary<string, long?> ParseSurfaceSizes(string html)
    {
        var result = new Dictionary<string, long?>(StringComparer.Ordinal);
        // Microsoft's downloadFile JSON array associates each size with its specific URL.
        var match = Regex.Match(html, "\"downloadFile\"\\s*:\\s*", RegexOptions.None, TimeSpan.FromSeconds(2));
        if (!match.Success) return result;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(match.Index + match.Length)..]));
            using var json = JsonDocument.ParseValue(ref reader);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var file in json.RootElement.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                    !file.TryGetProperty("size", out var size)) continue;
                result[url.GetString()!] = Size(size.ToString());
            }
        }
        catch (JsonException)
        {
            /* Size metadata is optional; links can still be browsed. */
        }

        return result;
    }

    public static List<DriverCatalogPackageDto> ParseSurfacePackages(string html, string source)
    {
        var sizes = ParseSurfaceSizes(html);
        html = WebUtility.HtmlDecode(html.Replace("\\u002F", "/").Replace("\\/", "/"));
        var matches = Regex.Matches(html, @"https://download\.microsoft\.com/[^\s""'<>\\]+?\.(?:msi|zip)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return matches.Select(m => m.Value).Distinct().Select(url =>
        {
            var file = FileName(url);
            var parts = Path.GetFileNameWithoutExtension(file).Split('_');
            var win = Array.FindIndex(parts, p => p.StartsWith("Win", StringComparison.OrdinalIgnoreCase));
            var os = win >= 0 ? string.Join(" ", parts.Skip(win).Take(2)) : "See package details";
            var release = win >= 0 && parts.Length > win + 2 ? parts[win + 2] : file;
            return new DriverCatalogPackageDto(DriverVendor.Microsoft, "Surface", os, file.Contains("arm", StringComparison.OrdinalIgnoreCase) ? "ARM64" : "See model", release,
                null, file, url, null, source) { DownloadSizeBytes = sizes.GetValueOrDefault(url) };
        }).ToList();
    }
}
