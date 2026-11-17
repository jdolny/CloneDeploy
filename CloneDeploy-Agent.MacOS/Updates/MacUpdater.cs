using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

public sealed record MacUpdateRetry(string Hash, DateTimeOffset RetryAfter);

public sealed class MacUpdater(MacConfiguration configuration, MacIdentity identity, MacStateStore store)
{
    private const int MaximumPackageEntries = 4096;
    private const int MaximumManifestBytes = 4096;

    private const int DownloadBufferBytes = 65536;
    private const int MaximumErrorCharacters = 2048;
    private const uint MachO64Magic = 0xfeedfacf;
    private const uint Arm64CpuType = 0x100000c;

    public static string InstalledVersion => typeof(MacUpdater).Assembly.GetName().Version!.ToString();
    private static string ShortVersion => AgentUpdateOffer.ParseVersion(InstalledVersion).ToString();
    private string Folder => Path.Combine(store.DirectoryPath, "Updates");

    public static void MarkHealthy(MacStateStore store)
    {
        // Written only after an authenticated check-in; the independent installer waits for this.
        if (store.DirectoryPath != MacStateStore.DefaultDirectory) return;
        var folder = Path.Combine(store.DirectoryPath, "Updates");
        PrivateDirectory(folder);
        var path = Path.Combine(folder, "healthy-" + ShortVersion);
        RejectLinks(path);
        using var file = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }
    public async Task CheckAsync(AgentUpdateOffer? offer, CancellationToken ct)
    {
        if (offer is null) return;
        if (offer.Architecture != "arm64")
        {
            Console.WriteLine($"Mac update skipped: prepared architecture '{offer.Architecture}' is not arm64.");
            return;
        }
        if (!offer.IsNewerThan(InstalledVersion))
        {
            if (AgentUpdateOffer.ParseVersion(offer.Version) < AgentUpdateOffer.ParseVersion(InstalledVersion))
                Console.WriteLine($"Mac update skipped: installed {InstalledVersion} is newer than prepared {offer.Version}; automatic downgrades are disabled. Prepare a matching or newer Mac release on the server.");
            return;
        }
        if (!OperatingSystem.IsMacOS() || store.DirectoryPath != MacStateStore.DefaultDirectory ||
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != Path.Combine(store.DirectoryPath, "bin"))
        {
            Console.WriteLine($"Mac agent {offer.Version} is available; updates run only from the installed daemon.");
            return;
        }
        var active = store.Read<MacUpdateRetry>("mac-update-active.json");
        if (active?.RetryAfter > DateTimeOffset.UtcNow)
        {
            Console.WriteLine($"Mac update {InstalledVersion} -> {offer.Version} is waiting for the previous update handoff until {active.RetryAfter:O}. Check {Folder} for install.log/result.txt.");
            return;
        }
        // The CLI already requires root. A launchd job must be present before replacing it.
        await RunAsync("/bin/launchctl", ["print", "system/com.clonedeploy.agent"], ct);
        var retry = store.Read<MacUpdateRetry>("mac-update-retry.json");
        if (retry?.Hash == offer.Sha256 && retry.RetryAfter > DateTimeOffset.UtcNow)
        {
            Console.WriteLine($"Mac update {InstalledVersion} -> {offer.Version} was already attempted; next retry at {retry.RetryAfter:O}. Check earlier 'Mac update failed' messages and {Folder} for install.log/result.txt.");
            return;
        }
        store.Save("mac-update-retry.json", new MacUpdateRetry(offer.Sha256, DateTimeOffset.UtcNow.AddHours(1)));
        PrivateDirectory(Folder);
        var attempt = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
        PrivateDirectory(attempt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        var archive = Path.Combine(attempt, "package.pkg");
        using var certificate = identity.GetClientCertificate() ?? throw new InvalidDataException("Missing agent certificate.");
        using var handler = new HttpClientHandler();
        var options = configuration.Options(store.DirectoryPath);
        AgentServerTrust.Configure(handler, options);
        handler.ClientCertificates.Add(certificate);
        using var client = new HttpClient(new AgentResponseVerification(handler, options)) { BaseAddress = options.GetServerUri(), Timeout = TimeSpan.FromMinutes(15) };
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} Downloading Mac agent {offer.Version}.");
        using var response = await AgentResponseVerification.DownloadAsync(client, $"api/agent/v1/mac-updates/{offer.Sha256}", offer.Sha256, token);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await CopyVerifiedAsync(source, output, offer, token);
        var script = Path.Combine(attempt, "install-update.sh");
        using (var resource = typeof(MacUpdater).Assembly.GetManifestResourceStream("Install-MacUpdate.sh")!)
        using (var reader = new StreamReader(resource))
            await File.WriteAllTextAsync(script, (await reader.ReadToEndAsync(token)).Replace("\r\n", "\n"), token);
        var plist = Path.Combine(attempt, "com.clonedeploy.agent.update.plist");
        var document = new XDocument(new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", "com.clonedeploy.agent.update"),
            new XElement("key", "ProgramArguments"), new XElement("array", new[] { "/bin/bash", script, Path.GetFileName(attempt), AgentUpdateOffer.ParseVersion(offer.Version).ToString(), offer.Sha256 }.Select(x => new XElement("string", x))),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "UserName"), new XElement("string", "root"),
            new XElement("key", "Umask"), new XElement("integer", 63),
            new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(attempt, "install.log")),
            new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(attempt, "install.log")))));
        await File.WriteAllTextAsync(plist, document.ToString(), token);
        // A completed helper remains registered. This is a separate launchd job so stopping
        // the main daemon cannot kill the process performing replacement/rollback.
        await RunAsync("/bin/launchctl", ["bootout", "system/com.clonedeploy.agent.update"], token, allowFailure: true);
        // New binaries may check in before the previous helper finishes its health check.
        // Keep them from interrupting that helper if another release was prepared meanwhile.
        store.Save("mac-update-active.json", new MacUpdateRetry(offer.Sha256, DateTimeOffset.UtcNow.AddMinutes(5)));
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} Starting update helper: system/com.clonedeploy.agent.update; plist: {plist}");
        await RunAsync("/bin/launchctl", ["bootstrap", "system", plist], token);
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} Mac update handed to launchd; installation log: {attempt}/install.log");
    }
    public static async Task CopyVerifiedAsync(Stream source, Stream destination, AgentUpdateOffer offer, CancellationToken ct)
    {
        offer.Validate();
        if (offer.Architecture != "arm64") throw new InvalidDataException("Mac update must be ARM64.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DownloadBufferBytes]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > offer.Length) throw new InvalidDataException("Mac update exceeds its advertised size.");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != offer.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(offer.Sha256)))
            throw new InvalidDataException("Mac update size or SHA-256 does not match the prepared release.");
    }
    public static async Task ExtractAsync(string archive, string destination, string version, CancellationToken ct)
    {
        if (Directory.Exists(destination)) throw new InvalidDataException("Update staging directory must be new.");
        PrivateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        await using var input = File.OpenRead(archive);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        long total = 0; int count = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++count > MaximumPackageEntries) throw new InvalidDataException("Too many Mac package entries.");
            var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (name is "" or ".") { if (entry.EntryType == TarEntryType.Directory) continue; }
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                name.Split('/').Any(x => x == "..") || !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("Unsafe or duplicate Mac package entry.");
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Package entry escapes staging.");
            if (entry.EntryType == TarEntryType.Directory) { PrivateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new InvalidDataException("Links and special files are not allowed in Mac updates.");
            total = checked(total + entry.Length);
            if (total > AgentUpdateOffer.MaximumLength) throw new InvalidDataException("Expanded Mac package exceeds its size limit.");
            PrivateDirectory(Path.GetDirectoryName(target)!);
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await entry.DataStream.CopyToAsync(file, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        foreach (var required in new[] { "CloneDeploy-Agent.MacOS", "CloneDeploy-Agent.MacOS.dll", "CloneDeploy-Agent.MacOS.runtimeconfig.json", "libhostfxr.dylib", "libcoreclr.dylib", "release.json" })
            if (!File.Exists(Path.Combine(destination, required))) throw new InvalidDataException("Incomplete Mac runtime package.");
        if (new FileInfo(Path.Combine(destination, "release.json")).Length > MaximumManifestBytes) throw new InvalidDataException("Oversized Mac release manifest.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "release.json"), ct));
        if (AgentUpdateOffer.ParseVersion(manifest.RootElement.GetProperty("Version").GetString()!) != AgentUpdateOffer.ParseVersion(version))
            throw new InvalidDataException("Mac package manifest version mismatch.");
        using var executable = File.OpenRead(Path.Combine(destination, "CloneDeploy-Agent.MacOS"));
        var header = new byte[8]; executable.ReadExactly(header);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) != MachO64Magic ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Arm64CpuType)
            throw new InvalidDataException("Mac update is not an Apple Silicon executable.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(destination, "CloneDeploy-Agent.MacOS"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateDirectory(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
    private static void RejectLinks(string path)
    {
        for (var info = new FileInfo(path); info is not null; info = info.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if (info.Attributes != (FileAttributes)(-1) && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update paths must not contain symbolic links.");
    }
    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Mac update helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout; var error = (await stderr).Trim();
        if (!allowFailure && process.ExitCode != 0)
        {
            if (error.Length > MaximumErrorCharacters) error = error[..MaximumErrorCharacters];
            throw new IOException($"Mac update command {Path.GetFileName(executable)} {arguments.FirstOrDefault()} failed with exit {process.ExitCode}. {error}");
        }
        return output;
    }
}
