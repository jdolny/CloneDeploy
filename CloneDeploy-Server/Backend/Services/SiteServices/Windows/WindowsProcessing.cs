using System.Text.Json;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.Extensions.Options;
namespace CloneDeploy_Server.Backend.Services;

public sealed class WindowsProcessing(WindowsWorkQueue queue, IOptions<DriverPreparationOptions> options)
{
    public async Task<int?> ExtractAsync(string source, string destination, DriverVendor? vendor, string? url, CancellationToken ct)
    {
        if (await queue.HostAsync(WindowsWorkerRoles.Drivers, ct) is null) return null;
        var id = await queue.EnqueueAsync("Driver", JsonSerializer.Serialize(new DriverWorkRequest(vendor is null ? null : (int)vendor, url)),
            new Dictionary<string, string> { ["source" + Path.GetExtension(source).ToLowerInvariant()] = source }, ct);
        try
        {
            var job = await queue.WaitAsync(id, ct);
            var output = Required(job, "prepared.zip");
            // Revalidate archive paths, expansion limits and INF contents on main before normal preparation publication.
            return await new DriverPackageExtractor(options).ExtractAsync(queue.Output(id, job.Lease, output.Name), destination, null, null, ct);
        }
        finally { await queue.ForgetAsync(id); }
    }
    public async Task<string?> CatalogAsync(byte[] bytes, CancellationToken ct)
    {
        if (await queue.HostAsync(WindowsWorkerRoles.Drivers, ct) is null) return null;
        var temporary = Path.Combine(queue.TemporaryDirectory(), "catalog-" + Guid.NewGuid().ToString("N") + ".cab");
        Guid? id = null;
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            id = await queue.EnqueueAsync("Catalog", "{}", new Dictionary<string, string> { ["catalog.cab"] = temporary }, ct);
            var job = await queue.WaitAsync(id.Value, ct); var output = Required(job, "catalog.xml");
            if (output.Length > 32 * 1024 * 1024) throw new InvalidDataException("Returned catalog exceeds its size limit.");
            return await File.ReadAllTextAsync(queue.Output(id.Value, job.Lease, output.Name), ct);
        }
        finally { File.Delete(temporary); if (id is not null) await queue.ForgetAsync(id.Value); }
    }

    private static WindowsWorkFile Required(WindowsWorkerJob job, string name)
    {
        var files = JsonSerializer.Deserialize<WindowsWorkFile[]>(job.OutputsJson)!;
        if (files.Length != 1 || files[0].Name != name) throw new InvalidDataException("Windows worker returned unexpected files.");
        return files[0];
    }
}
