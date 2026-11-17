using System.Diagnostics;
using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy_Server.WinPe;
using Microsoft.Extensions.Options;
namespace CloneDeploy.SiteService;

public sealed class WindowsWorkEngine(SiteLocalStore store, IConfiguration configuration) : IDisposable
{
    private const int MaximumCatalogBytes = 32 * 1024 * 1024;

    public BuildStorage Builds { get; } = new(Path.Combine(store.DirectoryPath, "WindowsBuilds")) { SecureWrites = true };
    private WinPeOptions Options => configuration.GetSection("WinPe").Get<WinPeOptions>() ?? new();
    private ProtectedBuildRunner? runner;
    private readonly object runnerGate = new();
    public ProtectedBuildRunner Builder { get { lock (runnerGate) return runner ??= new(Builds, Options, "CloneDeploy-WindowsWorker-WinPe"); } }
    public BuildSetupEngine Setup => Builder.Setup;
    public void Dispose() => runner?.Dispose();
    public BuilderHealthDto Health()
    {
        var health = Builder.Health();
        if (Directory.Exists(Builds.Root) && Directory.EnumerateDirectories(Builds.Root).Any(x => File.Exists(Path.Combine(x, "cleanup-required.txt"))))
            return health with { Ready = false, Message = "An interrupted WinPE mount needs cleanup on the Windows worker." };
        return health;
    }
    public Dictionary<string, string> Logs(WindowsWorkOffer job)
    {
        var path = job.Kind == "WinPe" ? Builds.FilePath(job.Id, "build.log") : job.Kind == "WinPeSetup" ? SetupFiles.PathFor(Builds, job.Id, "setup.log") : null;
        return path is not null && File.Exists(path) ? new() { [Path.GetFileName(path)] = path } : new();
    }
    public string Progress(WindowsWorkOffer job) => job.Kind == "WinPe" ? Builds.Status(job.Id).Message :
        job.Kind == "WinPeSetup" ? SetupFiles.Status(Builds, job.Id).Message : "Processing " + job.Kind;
    public async Task<(string? Result, Dictionary<string, string> Outputs)> RunAsync(WindowsWorkOffer job, string folder, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new InvalidOperationException("This worker requires 64-bit Windows.");
        var outputs = new Dictionary<string, string>(); string? result = null;
        switch (job.Kind)
        {
            case "WinPe":
                await Builder.RecoverPendingAsync();
                var request = JsonSerializer.Deserialize<BuildRequestDto>(job.Request) ?? throw new InvalidDataException("Missing build request.");
                if (request.Id != job.Id) throw new InvalidDataException("Build ID mismatch.");
                var directory = Builds.Job(job.Id);
                if (Directory.Exists(directory))
                {
                    // Interrupted attempts must release their own DISM mounts before any workspace cleanup.
                    await Builder.RecoverAsync(job.Id); BuildStorage.RejectLinks(directory); Directory.Delete(directory, true);
                }
                Directory.CreateDirectory(directory);
                using (BuilderSecurity.PinDirectory(folder))
                using (BuilderSecurity.PinDirectory(directory))
                    foreach (var file in job.Inputs)
                        await ProtectedBuildRunner.Publish(Path.Combine(folder, file.Name), Builds.FilePath(job.Id, file.Name), ct);
                var health = Health(); if (!health.Ready) throw new InvalidOperationException(health.Message);
                await Builder.BuildAsync(request, ct);
                var status = Builds.Status(job.Id);
                if (status.State != "Ready") throw new InvalidDataException("Build did not complete.");
                foreach (var artifact in status.Artifacts) outputs.Add(artifact.Name, Builds.FilePath(job.Id, artifact.Name));
                if (File.Exists(Builds.FilePath(job.Id, "build.log"))) outputs.Add("build.log", Builds.FilePath(job.Id, "build.log"));
                result = JsonSerializer.Serialize(status); break;
            case "WinPeSetup":
                await Builder.RecoverPendingAsync();
                var setup = JsonSerializer.Deserialize<SetupRequestDto>(job.Request) ?? throw new InvalidDataException("Missing setup request.");
                if (setup.Id != job.Id) throw new InvalidDataException("Setup ID mismatch.");
                Directory.CreateDirectory(SetupFiles.DirectoryPath(Builds, job.Id));
                await Builder.RunSetupAsync(setup, ct);
                result = JsonSerializer.Serialize(SetupFiles.Status(Builds, job.Id));
                foreach (var name in new[] { "setup.log" })
                    if (File.Exists(SetupFiles.PathFor(Builds, job.Id, name))) outputs.Add(name, SetupFiles.PathFor(Builds, job.Id, name));
                break;
            case "Driver":
                var driver = JsonSerializer.Deserialize<DriverWorkRequest>(job.Request)!;
                var extractor = new DriverPackageExtractor(Microsoft.Extensions.Options.Options.Create(configuration.GetSection("DriverPreparation").Get<DriverPreparationOptions>() ?? new()));
                var destination = Path.Combine(folder, "extracted");
                var count = await extractor.ExtractAsync(Path.Combine(folder, job.Inputs.Single().Name), destination,
                    driver.Vendor is { } vendor ? (DriverVendor)vendor : null, driver.CatalogUrl, ct);
                var archive = Path.Combine(folder, "prepared.zip"); await extractor.CreateArchiveAsync(destination, archive, ct);
                outputs.Add("prepared.zip", archive); result = count.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "Catalog":
                var xml = Path.Combine(folder, "catalog.xml");
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Path.Combine(folder, "catalog.cab")); start.ArgumentList.Add(xml);
                using (var process = Process.Start(start) ?? throw new IOException("Could not start catalog extraction."))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
                    try { await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr); }
                    catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
                    if (process.ExitCode != 0 || !File.Exists(xml) || new FileInfo(xml).Length > MaximumCatalogBytes) throw new IOException("Catalog extraction failed or exceeded the size limit.");
                }
                outputs.Add("catalog.xml", xml); break;
            case "MsiVersion": result = AgentMsiEditor.ReadVersion(Path.Combine(folder, "template.msi")); break;
            case "MsiArchitecture": result = AgentMsiEditor.ReadArchitecture(Path.Combine(folder, "template.msi")); break;
            default: throw new InvalidDataException("Unsupported work type.");
        }
        return (result, outputs);
    }
}
