using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace CloneDeploy_Agent.MacOS;

/// <summary>One daemon per state directory; credentials are readable only by its owner (root in an installed daemon).</summary>
public sealed class MacStateStore : IDisposable
{
    private const int MaximumStateFileBytes = 128 * 1024;

    public const string DefaultDirectory = "/Library/Application Support/CloneDeploy/Agent";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly FileStream? instanceLock;
    private readonly bool readOnly;
    public string DirectoryPath { get; }
    public MacStateStore(string directory, bool readOnly = false)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("State directory must be an absolute path.");
        this.readOnly = readOnly;
        DirectoryPath = Path.GetFullPath(directory);
        RejectLinks(DirectoryPath);
        if (readOnly)
        {
            if (!Directory.Exists(DirectoryPath)) throw new DirectoryNotFoundException("Agent has not been configured.");
            if (File.Exists(Path.Combine(DirectoryPath, "configuration-pending.json")))
                throw new IOException("Configuration recovery is pending; start the daemon before reading status.");
            return;
        }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath); // Isolated protocol tests only; CLI rejects Windows.
        else { Directory.CreateDirectory(DirectoryPath, DirectoryMode); File.SetUnixFileMode(DirectoryPath, DirectoryMode); }
        var path = Path.Combine(DirectoryPath, "agent.lock");
        RejectLinks(path);
        instanceLock = OpenPrivate(path, FileMode.OpenOrCreate);
        try { RecoverConfiguration(); } catch { instanceLock.Dispose(); throw; }
    }
    private static void RejectLinks(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && item.Attributes != (FileAttributes)(-1))
                throw new IOException("Agent state paths must not contain symbolic links.");
    }
    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
        var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, FileModeBits);
        return stream;
    }
    public T? Read<T>(string name) where T : class
    {
        if (Path.GetFileName(name) != name) throw new ArgumentException("State file name must not contain a directory.");
        var path = Path.Combine(DirectoryPath, name);
        RejectLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumStateFileBytes) throw new InvalidDataException("Agent state file exceeds its size limit.");
        if (!readOnly && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, FileModeBits);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty agent state file.");
    }
    public void Save<T>(string name, T value)
    {
        if (readOnly) throw new InvalidOperationException("Status store is read-only.");
        if (Path.GetFileName(name) != name) throw new ArgumentException("State file name must not contain a directory.");
        var destination = Path.Combine(DirectoryPath, name);
        RejectLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = OpenPrivate(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void ConfigurePackage(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > MaximumStateFileBytes)
            throw new InvalidDataException("Invalid prepared configuration file.");
        var configuration = JsonSerializer.Deserialize<MacConfiguration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty prepared configuration.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var certificatePath = Path.Combine(Path.GetDirectoryName(path)!, "server-ca.cer");
        string? temporary = null;
        try
        {
            if (document.RootElement.TryGetProperty("ServerCaCertificateBase64", out var embedded))
            {
                var bytes = Convert.FromBase64String(embedded.GetString() ?? "");
                using var certificate = X509CertificateLoader.LoadCertificate(bytes);
                temporary = Path.Combine(DirectoryPath, "enrollment-ca-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = OpenPrivate(temporary, FileMode.CreateNew)) { file.Write(bytes); file.Flush(true); }
                certificatePath = temporary;
            }
            if (!File.Exists(certificatePath)) throw new InvalidDataException("Enrollment configuration is missing its server CA certificate.");
            // Enrollment never supplies another machine's identity or an arbitrary certificate path.
            ApplyPackageConfiguration(configuration with { DeviceName = Environment.MachineName,
                ServerCaCertificatePath = certificatePath });
        }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    private sealed record ConfigurationChange(MacConfiguration Configuration, MacIdentityState? Identity);
    private void RecoverConfiguration()
    {
        var pending = Read<ConfigurationChange>("configuration-pending.json");
        if (pending is null) return;
        Save("configuration.json", pending.Configuration);
        if (pending.Identity is not null) Save("identity.json", pending.Identity);
        File.Delete(Path.Combine(DirectoryPath, "configuration-pending.json"));
    }
    private static string? TrustHash(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }
    public void ApplyPackageConfiguration(MacConfiguration configuration)
    {
        if (readOnly) throw new InvalidOperationException("Status store is read-only.");
        var options = configuration.Options(DirectoryPath); // Validate before touching active state.
        var previous = Read<MacConfiguration>("configuration.json");
        var identity = Read<MacIdentityState>("identity.json");
        if (identity is not null && identity.SchemaVersion != 1) throw new InvalidDataException("Unsupported identity version.");
        var newTrust = TrustHash(options.ServerCaCertificatePath);
        string? oldTrust;
        try { oldTrust = TrustHash(previous?.ServerCaCertificatePath); }
        catch (Exception ex) when (ex is IOException or CryptographicException) { oldTrust = "unreadable"; }
        if (newTrust is not null)
        {
            var destination = Path.Combine(DirectoryPath, "server-ca-" + newTrust + ".cer");
            RejectLinks(destination);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = OpenPrivate(temporary, FileMode.CreateNew))
                { file.Write(File.ReadAllBytes(options.ServerCaCertificatePath!)); file.Flush(true); }
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            configuration = configuration with { ServerCaCertificatePath = destination };
        }
        configuration = configuration with { ServerUrl = options.ServerUrl };
        if (identity is not null)
        {
            var oldUrl = new Uri(identity.ServerUrl);
            var newUrl = options.GetServerUri();
            var reset = previous is null || oldUrl.Scheme != newUrl.Scheme || !string.Equals(oldUrl.IdnHost, newUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
                || oldUrl.AbsolutePath != newUrl.AbsolutePath || oldTrust != newTrust;
            identity = reset
                ? MacIdentity.CreateState(options.ServerUrl, options.DeviceName) with { RequestReset = true }
                : identity with { ServerUrl = options.ServerUrl };
        }
        // Durable intent permits roll-forward after a crash between the two atomic file replacements.
        Save("configuration-pending.json", new ConfigurationChange(configuration, identity));
        RecoverConfiguration();
    }
    public void Configure(MacConfiguration configuration)
    {
        var identity = Read<MacIdentityState>("identity.json");
        if (identity is not null && identity.ServerUrl != configuration.Options(DirectoryPath).ServerUrl)
            throw new InvalidDataException("This identity belongs to another server. Install a configured package.");
        ApplyPackageConfiguration(configuration);
    }
    public void ResetEnrollment(bool full)
    {
        var config = Read<MacConfiguration>("configuration.json") ?? throw new InvalidOperationException("Install a configured package first.");
        var options = config.Options(DirectoryPath);
        var previous = Read<MacIdentityState>("identity.json");
        if (previous is not null && previous.SchemaVersion != 1) throw new InvalidDataException("Unsupported identity version.");
        var replacement = MacIdentity.CreateState(options.ServerUrl, Environment.MachineName) with { RequestReset = true };
        if (!full && previous is not null) replacement = replacement with { InstallationId = previous.InstallationId };
        Save("identity.json", replacement);
    }
    public void Dispose() => instanceLock?.Dispose();
}
