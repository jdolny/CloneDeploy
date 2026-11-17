using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CloneDeploy_Server.WinPe;

/// <summary>Validates Imager downloads and installs the download-on-boot bootstrap into a mounted variant.</summary>
public sealed class ImagerBundle(string packagePath, string? serverUrl)
{
    private const int CopyBufferBytes = 81_920;
    private const int MaximumArchiveEntries = 10_000;
    private const long MaximumExpandedArchiveBytes = 1024L * 1024 * 1024;
    private const int MaximumDependencyManifestBytes = 4 * 1024 * 1024;
    private const int MaximumBootstrapCaCharacters = 65_536;

    public string Architecture { get; init; } = "x64";
    public string? Arm64PackagePath { get; init; }
    public string[] TimeServers { get; init; } = ["time.windows.com"];
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly object architectureGate = new();
    private ImagerBundle? arm64Bundle;
    private sealed record DownloadMetadata(long Length, DateTime Written, DateTime Created, string Version, string Sha256);
    private DownloadMetadata? downloadMetadata;
    private ImagerBundle Arm64Bundle()
    {
        lock (architectureGate)
            return arm64Bundle ??= new(Arm64PackagePath ?? Path.Combine(Path.GetDirectoryName(packagePath)!, "win-arm64.zip"), serverUrl) { Architecture = "arm64" };
    }
    public ImagerBundle ForArchitecture(string architecture) => architecture switch
    {
        "x64" => this,
        "arm64" => Arm64Bundle(),
        _ => throw new InvalidDataException("Unsupported Imager architecture.")
    };

    public static async Task InstallArm64Async(ImagerBuildDto imager, string mount, CancellationToken ct)
    {
        var url = NormalizeServerUrl(imager.ServerUrl) ?? throw new InvalidDataException("The ARM64 bootstrap requires a server URL.");
        await InstallArm64ToolsAsync(mount, ct);
        await InstallBootstrapAsync(mount, url, ct, imager.TrustedRootCertificate, "arm64", imager.TimeServers);
    }
    private const string StartNetPrefix = "@echo off\r\n" +
        "\"%SystemRoot%\\System32\\regsvr32.exe\" /s \"%SystemRoot%\\System32\\winfsp-x64.dll\"\r\n" +
        "if errorlevel 1 echo WinFsp registration failed.\r\n" +
        "if errorlevel 1 exit /b 1\r\nwpeinit\r\n";

    private static Task InstallWinFspAsync(string mount, CancellationToken ct) => InstallWinFspFilesAsync(mount, "x64", ct);

    public static async Task InstallArm64ToolsAsync(string mount, CancellationToken ct)
    {
        await InstallWinFspFilesAsync(mount, "a64", ct);
        await WimlibBundle.InstallAsync(mount, true, ct);
        await UdpcastBundle.InstallAsync(mount, true, ct);
        var startnet = Path.Combine(mount, "Windows", "System32", "startnet.cmd");
        BuildStorage.RejectLinks(startnet);
        await File.WriteAllTextAsync(startnet,
            StartNetPrefix.Replace("winfsp-x64.dll", "winfsp-a64.dll", StringComparison.Ordinal) +
            "echo CloneDeploy ARM64 WinPE tools ready.\r\n", Encoding.ASCII, ct);
    }

    private static async Task InstallWinFspFilesAsync(string mount, string architecture, CancellationToken ct)
    {
        foreach (var name in new[] { $"winfsp-{architecture}.dll", $"winfsp-{architecture}.sys", "License.txt" })
        {
            var destination = name == "License.txt" ? Path.Combine(mount, "CloneDeploy", "WinFsp", name) : Path.Combine(mount, "Windows", "System32", name);
            BuildStorage.RejectLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = typeof(ImagerBundle).Assembly.GetManifestResourceStream("CloneDeploy.WinFsp." + name)
                ?? throw new InvalidDataException("Missing WinFsp build resource: " + name);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }
    }
    public ImagerBuildDto Bootstrap(string? variantServerUrl) => new(
        NormalizeServerUrl(variantServerUrl) ?? NormalizeServerUrl(serverUrl) ??
        throw new InvalidDataException("Enter the CloneDeploy HTTPS server URL in the boot environment configuration.")) { TimeServers = ValidateTimeServers(TimeServers) };

    private static string[] ValidateTimeServers(string[]? servers)
    {
        if (servers is null || servers.Length > 4 || servers.Any(x => string.IsNullOrWhiteSpace(x) || Uri.CheckHostName(x) == UriHostNameType.Unknown))
            throw new InvalidDataException("Configure up to four WinPE time servers using host names or IP addresses, without URLs or ports.");
        return servers.ToArray();
    }

    // Hold the same read-only handle through validation, hashing and HTTP delivery.
    public async Task<(FileStream Stream, string Version, string Sha256)> OpenDownloadAsync(CancellationToken ct)
    {
        BuildStorage.RejectLinks(packagePath);
        var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, true);
        try
        {
            await metadataGate.WaitAsync(ct);
            try
            {
                var info = new FileInfo(packagePath);
                var cached = downloadMetadata;
                if (cached is null || cached.Length != stream.Length || cached.Written != info.LastWriteTimeUtc || cached.Created != info.CreationTimeUtc)
                {
                    var version = Inspect(packagePath, Architecture);
                    var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
                    cached = new(stream.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, version, hash);
                    downloadMetadata = cached;
                }
                stream.Position = 0;
                return (stream, cached.Version, cached.Sha256);
            }
            finally { metadataGate.Release(); }
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static readonly string[] RequiredFiles =
    [
        "CloneDeploy-Imager.exe", "CloneDeploy-Imager.dll", "CloneDeploy-Imager.deps.json",
        "CloneDeploy-Imager.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
        "System.Private.CoreLib.dll", "Spectre.Console.dll"
    ];

    public static Task ValidateAsync(BuildStorage storage, Guid id, ImagerBuildDto? imager, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (imager is null || NormalizeServerUrl(imager.ServerUrl) is null)
            throw new InvalidDataException("The Imager bootstrap requires a server URL.");
        _ = RootCertificate(imager.TrustedRootCertificate);
        return Task.CompletedTask;
    }

    public static async Task InstallAsync(BuildStorage storage, Guid id, ImagerBuildDto imager, string mount,
        CancellationToken ct)
    {
        await ValidateAsync(storage, id, imager, ct);
        await InstallWinFspAsync(mount, ct);
        await WimlibBundle.InstallAsync(mount, false, ct);
        await UdpcastBundle.InstallAsync(mount, false, ct);
        await InstallBootstrapAsync(mount, imager.ServerUrl!, ct, imager.TrustedRootCertificate, "x64", imager.TimeServers);
    }

    public static string? NormalizeServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("The Imager server URL must be an HTTPS application address without credentials, a query, or a fragment.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static string Inspect(string archive) => Inspect(archive, "x64");

    private static string Inspect(string archive, string architecture)
    {
        using var zip = ZipFile.OpenRead(archive);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long length = 0;
        if (zip.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("Imager bundle contains too many files.");
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x is "." or "..") ||
                name.Any(char.IsControl) || name.Split('/').Any(x => x.EndsWith(' ') || x.EndsWith('.')))
                throw new InvalidDataException("Invalid path in Imager bundle.");
            length = checked(length + entry.Length);
            if (length > MaximumExpandedArchiveBytes) throw new InvalidDataException("Imager bundle exceeds the 1 GB expanded limit.");
            if (name.EndsWith('/')) continue;
            if (!files.TryAdd(name, entry)) throw new InvalidDataException("Duplicate file in Imager bundle.");
        }
        foreach (var name in RequiredFiles)
            if (!files.TryGetValue(name, out var file) || file.Length == 0)
                throw new InvalidDataException("The bundled Imager is incomplete: " + name);
        if (files.ContainsKey("imager.json")) throw new InvalidDataException("The release bundle must not contain machine configuration or credentials.");
        if (files["CloneDeploy-Imager.deps.json"].Length > MaximumDependencyManifestBytes) throw new InvalidDataException("Invalid Imager dependency manifest.");
        using var stream = files["CloneDeploy-Imager.deps.json"].Open();
        using var dependencies = JsonDocument.Parse(stream);
        var runtimeTarget = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        if (!runtimeTarget.EndsWith("/win-" + architecture, StringComparison.Ordinal))
            throw new InvalidDataException("The Imager bundle must target win-" + architecture + ".");
        var target = dependencies.RootElement.GetProperty("targets").GetProperty(runtimeTarget);
        string? version = null;
        foreach (var library in target.EnumerateObject())
        {
            if (library.Name.StartsWith("CloneDeploy-Imager/", StringComparison.Ordinal)) version = library.Name["CloneDeploy-Imager/".Length..];
            foreach (var category in new[] { "runtime", "native", "resources" })
            {
                if (!library.Value.TryGetProperty(category, out var assets)) continue;
                foreach (var asset in assets.EnumerateObject())
                {
                    var path = category == "resources" ? asset.Name : Path.GetFileName(asset.Name);
                    if (!files.TryGetValue(path, out var file) || file.Length == 0)
                        throw new InvalidDataException("Missing Imager dependency: " + path);
                }
            }
        }
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Imager version is missing from the dependency manifest.");
        return version;
    }

    private static byte[]? RootCertificate(string? encoded)
    {
        if (encoded is null) return null;
        try
        {
            if (encoded.Length > MaximumBootstrapCaCharacters) throw new InvalidDataException("The bootstrap CA is too large.");
            var bytes = Convert.FromBase64String(encoded);
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(bytes);
            if (certificate.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true ||
                DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                throw new InvalidDataException("The bootstrap trust certificate must be a valid CA certificate.");
            return certificate.RawData;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        { throw new InvalidDataException("Invalid bootstrap CA certificate.", ex); }
    }

    private static async Task InstallBootstrapAsync(string mount, string serverUrl, CancellationToken ct, string? trustedRoot, string architecture, string[] timeServers)
    {
        timeServers = ValidateTimeServers(timeServers);
        var directory = Path.Combine(mount, "CloneDeploy");
        BuildStorage.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        if (RootCertificate(trustedRoot) is { } root)
        {
            var caPath = Path.Combine(directory, "CloneDeploy-Root-CA.cer");
            BuildStorage.RejectLinks(caPath);
            await using var caFile = new FileStream(caPath, FileMode.CreateNew, FileAccess.Write);
            await caFile.WriteAsync(root, ct);
        }
        var script = Path.Combine(directory, "Start-Imager.ps1");
        BuildStorage.RejectLinks(script);
        await using (var input = typeof(ImagerBundle).Assembly.GetManifestResourceStream("CloneDeploy.Start-Imager.ps1")
            ?? throw new InvalidDataException("The Imager bootstrap script is missing from the builder."))
        await using (var output = new FileStream(script, FileMode.CreateNew, FileAccess.Write))
            await input.CopyToAsync(output, ct);
        var verifier = Path.Combine(directory, "ImagerBootstrapSignature.cs");
        BuildStorage.RejectLinks(verifier);
        await using (var input = typeof(ImagerBundle).Assembly.GetManifestResourceStream("CloneDeploy.ImagerBootstrapSignature.cs")
            ?? throw new InvalidDataException("The Imager signature verifier is missing from the builder."))
        await using (var output = new FileStream(verifier, FileMode.CreateNew, FileAccess.Write))
            await input.CopyToAsync(output, ct);
        var timeSync = Path.Combine(directory, "ImagerTimeSync.cs");
        BuildStorage.RejectLinks(timeSync);
        await using (var input = typeof(ImagerBundle).Assembly.GetManifestResourceStream("CloneDeploy.ImagerTimeSync.cs")
            ?? throw new InvalidDataException("The Imager time-sync helper is missing from the builder."))
        await using (var output = new FileStream(timeSync, FileMode.CreateNew, FileAccess.Write))
            await input.CopyToAsync(output, ct);
        foreach (var name in new[] { "bootstrap.json", "imager.json" }) BuildStorage.RejectLinks(Path.Combine(directory, name));
        await File.WriteAllTextAsync(Path.Combine(directory, "bootstrap.json"), JsonSerializer.Serialize(new { ServerUrl = serverUrl, Architecture = architecture, TimeServers = timeServers }), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "imager.json"), JsonSerializer.Serialize(new { ServerUrl = serverUrl }), ct);
        var startnet = Path.Combine(mount, "Windows", "System32", "startnet.cmd");
        BuildStorage.RejectLinks(startnet);
        Directory.CreateDirectory(Path.GetDirectoryName(startnet)!);
        await File.WriteAllTextAsync(startnet, StartNetPrefix.Replace("winfsp-x64.dll", architecture == "arm64" ? "winfsp-a64.dll" : "winfsp-x64.dll", StringComparison.Ordinal) +
            "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%SystemDrive%\\CloneDeploy\\Start-Imager.ps1\"\r\n", Encoding.ASCII, ct);
    }
}
