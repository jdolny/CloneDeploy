using CloneDeploy_Imager.Diagnostics;
using Fsp;

namespace CloneDeploy_Imager.FfuFileSystem.Reading;

/// <summary>
/// Owns the temporary read-only WinFsp mount and releases its reader and callbacks on disposal.
/// </summary>
internal sealed class FfuReadMount : IDisposable
{
    private readonly FileSystemHost host;
    private readonly CancellationTokenSource stop;
    private readonly HttpsFfuReader reader;
    private readonly CancellationTokenRegistration cancelReader;
    public string FilePath { get; }
    public FfuReadMount(HttpsFfuReader reader, DiagnosticLog log, CancellationToken ct)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.reader = reader;
        cancelReader = stop.Token.Register(reader.Cancel);
        host = new FileSystemHost(new FfuReadFileSystem(reader, log, stop.Token));
        try
        {
            log.Write("Mounting the HTTPS FFU filesystem.");
            var result = host.Mount(null, Synchronized: false);
            if (result < 0) throw new IOException($"WinFsp mount failed (NTSTATUS 0x{result:X8}). Verify the WinFsp driver and DLL registration in WinPE.");
            FilePath = host.MountPoint().TrimEnd('\\') + "\\image.ffu";
            log.Write("Experimental read-only FFU mounted at " + FilePath);
        }
        catch (Exception ex) { log.Failure("WinFsp mount", ex); host.Dispose(); reader.Dispose(); cancelReader.Dispose(); stop.Dispose(); throw; }
    }
    public void Dispose()
    { stop.Cancel(); try { host.Dispose(); } finally { reader.Dispose(); cancelReader.Dispose(); stop.Dispose(); } }
}
