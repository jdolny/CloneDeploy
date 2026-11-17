using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy.SiteServices;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

// Extraction commands are owned by the server, never supplied by a catalog or a UI field.
/// <summary>
/// Unpacks downloaded driver packages so their drivers can be prepared for use.
/// </summary>
public sealed class DriverPackageExtractor(IOptions<DriverPreparationOptions> options)
{
    public Func<string, string, DriverVendor?, string?, CancellationToken, Task<int?>>? RemoteExtract { get; init; }
    public async Task<int> ExtractAsync(string source, string destination, DriverVendor? vendor, string? catalogUrl, CancellationToken ct)
    {
        if (Path.GetExtension(source).ToLowerInvariant() != ".zip" && RemoteExtract is not null &&
            await RemoteExtract(source, destination, vendor, catalogUrl, ct) is { } remoteCount) return remoteCount;
        Directory.CreateDirectory(destination);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension == ".zip") await ExtractZipAsync(source, destination, ct);
        else if (extension == ".cab")
            await ExtractWithSevenZipAsync(source, destination, ct);
        else if (extension is ".exe" or ".msi")
        {
            if (!vendor.HasValue || !DriverCatalogClient.IsVendorDownload(vendor.Value, catalogUrl ?? ""))
                throw new InvalidOperationException("Automatic EXE/MSI extraction requires an import from the online library. For manual drivers, upload a ZIP of the extracted driver folder.");
            await VerifyPublisherAsync(source, vendor.Value, ct);
            if (extension == ".msi" && vendor == DriverVendor.Microsoft)
                await RunAsync(SystemTool("msiexec.exe"), ["/a", source, "/qn", "TARGETDIR=" + destination, "/norestart"], ct, () => InspectFiles(destination, ct));
            else if (extension == ".exe" && vendor is DriverVendor.HP or DriverVendor.Dell)
                await ExtractWithSevenZipAsync(source, destination, ct);
            else if (extension == ".exe" && vendor == DriverVendor.Lenovo)
            {
                // Keep the vendor's extraction-only path until its installer formats are verified with 7-Zip.
                await RunAsync(source, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/EXTRACT=YES", "/DIR=" + destination],
                    ct, () => InspectFiles(destination, ct));
            }
            else throw new InvalidOperationException("Only Surface MSI deployment packs are supported automatically.");
        }
        else throw new InvalidOperationException("Upload a ZIP containing the complete driver folder, or import a supported vendor deployment pack.");

        var files = InspectFiles(destination, ct);
        var infCount = 0;
        foreach (var file in files.Where(x => Path.GetExtension(x).Equals(".inf", StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(file).Length > 1024 * 1024) continue;
            var text = await File.ReadAllTextAsync(file, ct);
            if (text.Contains("[Version]", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("$Windows NT$", StringComparison.OrdinalIgnoreCase) || text.Contains("$Chicago$", StringComparison.OrdinalIgnoreCase))) infCount++;
        }
        if (infCount == 0) throw new InvalidOperationException("Extraction produced no Windows driver INFs. This package may require installation in Windows; upload an extracted INF driver pack instead.");
        return infCount;
    }

    public async Task CreateArchiveAsync(string directory, string archive, CancellationToken ct)
    {
        var files = InspectFiles(directory, ct);
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Fastest);
            await using var input = File.OpenRead(file);
            await using var output = entry.Open();
            await input.CopyToAsync(output, ct);
        }
    }

    private async Task ExtractZipAsync(string source, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > options.Value.MaxFiles) throw new InvalidOperationException("The driver archive contains too many entries.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            if (parts.Any(x => x is "." or "..") || parts[0].Length == 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidOperationException("The driver archive contains an unsafe path or symbolic link.");
            foreach (var part in parts.Where(x => x.Length > 0)) WindowsWorkFiles.Name(part);
            var path = Path.GetFullPath(Path.Combine(destination, Path.Combine(parts)));
            if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The driver archive contains an unsafe path.");
            if (parts[^1].Length == 0) { Directory.CreateDirectory(path); continue; }
            if (entry.Length > options.Value.MaxExpandedBytes - total) throw new InvalidOperationException("The expanded driver package exceeds the configured size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > options.Value.MaxExpandedBytes) throw new InvalidOperationException("The expanded driver package exceeds the configured size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }

    private List<string> InspectFiles(string directory, CancellationToken ct)
    {
        var files = new List<string>();
        var folders = new Stack<string>(); folders.Push(directory);
        long total = 0; var entries = 0;
        while (folders.TryPop(out var folder))
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (++entries > options.Value.MaxFiles) throw new InvalidOperationException("The extracted package contains too many entries.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("The extracted package contains a symbolic link.");
            if ((attributes & FileAttributes.Directory) != 0) { folders.Push(path); continue; }
            total += new FileInfo(path).Length;
            if (total > options.Value.MaxExpandedBytes) throw new InvalidOperationException("The expanded driver package exceeds the configured size limit.");
            files.Add(path);
        }
        return files;
    }

    private async Task ExtractWithSevenZipAsync(string source, string destination, CancellationToken ct)
    {
        // No installer fallback: archive failures stay visible and never execute the downloaded wrapper.
        await RunAsync(FindSevenZip(), ["x", source, "-o" + destination, "-y", "-bso0", "-bsp0"], ct, () => InspectFiles(destination, ct));
    }

    private string FindSevenZip()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.SevenZipPath))
        {
            var configured = options.Value.SevenZipPath;
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new InvalidOperationException("DriverPreparation:SevenZipPath must point to an existing 7z.exe using an absolute path.");
            return configured;
        }
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Enable a Windows driver worker for this package, or configure DriverPreparation:SevenZipPath with a compatible local extractor. Extracted ZIP uploads work on all platforms.");
        var directory = Path.Combine(AppContext.BaseDirectory, "Tools", "7zip");
        var bundled = Path.Combine(directory, "7z.exe");
        if (File.Exists(bundled) && File.Exists(Path.Combine(directory, "7z.dll"))) return bundled;
        throw new InvalidOperationException("The bundled 7-Zip extractor is missing. Restore the complete application deployment, including Tools/7zip, or configure DriverPreparation:SevenZipPath. An extracted driver ZIP can also be uploaded.");
    }

    private static string SystemTool(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This driver package requires a Windows server for extraction. Extracted ZIP uploads work on all platforms.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
    }

    public static void ValidatePublisherSignature(DriverVendor vendor, string status, string subject, string statusMessage)
    {
        if (!string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Windows could not validate the package signature ({status}): {statusMessage} The package was not executed.");
        var publisher = vendor switch
        {
            DriverVendor.Dell => @"Dell Inc\.|Dell Technologies Inc\.",
            DriverVendor.HP => @"HP Inc\.|Hewlett-Packard Company|Hewlett Packard",
            DriverVendor.Lenovo => @"Lenovo(?: \(Beijing\) Limited| \(United States\) Inc\.| Group Limited)?",
            DriverVendor.Microsoft => "Microsoft Corporation",
            _ => throw new InvalidOperationException("Unsupported driver manufacturer.")
        };
        if (!System.Text.RegularExpressions.Regex.IsMatch(subject, @"(?:^|,\s*)O=""?(?:" + publisher + @")(?:""?,|""?$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException($"The package has a valid signature, but its publisher is not recognized for {vendor}. Signer: {subject}. The package was not executed.");
    }

    private static async Task VerifyPublisherAsync(string source, DriverVendor vendor, CancellationToken ct)
    {
        var script = "$ErrorActionPreference = 'Stop'; $s = Get-AuthenticodeSignature -LiteralPath '" + source.Replace("'", "''") + "'; " +
            "@{Status=$s.Status.ToString(); Subject=$s.SignerCertificate.Subject; Message=$s.StatusMessage} | ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // A PowerShell 7 parent can pass module paths incompatible with Windows PowerShell.
        start.Environment["PSModulePath"] = SystemTool(@"WindowsPowerShell\v1.0\Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the package signature check.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows could not inspect the package signature. The signature-check process failed; the package was not executed.", new InvalidOperationException(await error));
        using var signature = System.Text.Json.JsonDocument.Parse(await output);
        var value = signature.RootElement;
        ValidatePublisherSignature(vendor, value.GetProperty("Status").GetString() ?? "Unknown",
            value.GetProperty("Subject").GetString() ?? "", value.GetProperty("Message").GetString() ?? "No details returned.");
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct, Action? inspect = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the driver extractor."); }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 740)
        { throw new InvalidOperationException("This vendor extractor requires administrator elevation. Upload an extracted driver ZIP or use an extraction worker with the required permissions.", exception); }
        using var processLifetime = process;
        try
        {
            var exited = process.WaitForExitAsync(ct);
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(1), ct));
                ct.ThrowIfCancellationRequested();
                inspect?.Invoke();
            }
            await exited;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Driver extraction failed (exit code {process.ExitCode}). Upload an extracted driver ZIP if this package needs a different extractor.");
    }
}
