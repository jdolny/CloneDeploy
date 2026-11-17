using CloneDeploy_Imager.Diagnostics;
using Fsp;

namespace CloneDeploy_Imager.FfuFileSystem.Writing;

/// <summary>
/// Owns the writable WinFsp mount, verifies pending uploads, and releases capture filesystem resources.
/// </summary>
internal sealed class FfuCaptureMount : IDisposable
{
    private readonly FileSystemHost host;
    private readonly FfuCaptureFileSystem fs;
    private readonly CancellationTokenSource stop;
    private readonly BufferedFfuCaptureFile file;
    public string FilePath { get; }
    public FfuCaptureMount(Func<CancellationToken, IFfuCaptureTransport> createFile, DiagnosticLog log, CancellationToken ct)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        file = new BufferedFfuCaptureFile(createFile(stop.Token), log);
        fs = new(file, log); host = new(fs);
        try
        {
            var status = host.Mount(null, Synchronized: true);
            if (status < 0) throw new IOException($"FFU upload mount failed (NTSTATUS 0x{status:X8}).");
            FilePath = host.MountPoint().TrimEnd('\\') + @"\capture.ffu";
            log.Write("HTTPS FFU capture mounted at " + FilePath);
        }
        catch { host.Dispose(); stop.Dispose(); throw; }
    }
    public void FlushAndVerify() { fs.EnsureHealthy(); file.Flush(); fs.EnsureHealthy(); }
    public void Dispose() { stop.Cancel(); try { host.Dispose(); } finally { file.Report(); stop.Dispose(); } }
}
