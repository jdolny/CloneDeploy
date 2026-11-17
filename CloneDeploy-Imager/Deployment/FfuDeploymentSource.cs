using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.FfuFileSystem.Reading;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Deployment;

// Keep a read handle open through DISM apply so the verified FFU cannot be replaced or written.
/// <summary>
/// Opens and optionally verifies an FFU source while retaining its read handle through deployment.
/// </summary>
internal sealed class FfuDeploymentSource : IAsyncDisposable
{
    private const int FileBufferBytes = 1024 * 1024;

    private readonly IDisposable connection;
    private readonly FileStream file;
    public string FilePath { get; }
    private FfuDeploymentSource(string path, IDisposable connection, FileStream file)
    { FilePath = path; this.connection = connection; this.file = file; }

    public static void Validate(DeploymentExecution image)
    {
        if (image.Format != "Ffu" || image.Direct is not { } direct || string.IsNullOrWhiteSpace(direct.Username) || direct.Password is null)
            throw new InvalidDataException("The server did not provide an FFU SMB connection with managed credentials.");
        SmbCaptureConnection.Validate(direct);
        if (!Regex.IsMatch(direct.RelativePath, @"\AImages\\[1-9][0-9]*\\[0-9a-fA-F]{32}\\image\.ffu\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid published FFU path.");
    }
    public static async Task<FfuDeploymentSource> OpenAsync(DeploymentExecution image,
        Func<SmbCapture, (string Path, IDisposable Connection)> connect, CancellationToken ct)
    {
        Validate(image);
        ct.ThrowIfCancellationRequested();
        var source = connect(image.Direct!);
        FileStream? file = null;
        try
        {
            file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != image.SizeBytes) throw new InvalidDataException("SMB FFU size differs from the assigned version.");
            if (image.Performance.VerifyImage)
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!hash.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SMB FFU checksum verification failed. The target disk has not been changed.");
            }
            return new(source.Path, source.Connection, file);
        }
        catch
        {
            if (file is not null) await file.DisposeAsync();
            source.Connection.Dispose();
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await file.DisposeAsync(); }
        finally { connection.Dispose(); }
    }

    public static async Task<FfuDeploymentSource> OpenHttpsAsync(DeploymentExecution image, HttpsFfuReader reader,
        CloneDeploy_Imager.Diagnostics.DiagnosticLog log, CancellationToken ct)
    {
        var mount = new FfuReadMount(reader, log, ct);
        FileStream? file = null;
        try
        {
            file = new FileStream(mount.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferBytes, FileOptions.Asynchronous);
            if (file.Length != image.SizeBytes) throw new InvalidDataException("HTTPS FFU size differs from the assigned version.");
            if (image.Performance.VerifyImage)
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!hash.Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("HTTPS FFU checksum verification failed. The target disk has not been changed.");
                log.Write("Deployment FFU checksum verified through HTTPS/WinFsp.");
            }
            else log.Write("Deployment FFU checksum pass skipped by server setting.");
            return new(mount.FilePath, mount, file);
        }
        catch
        {
            if (file is not null) await file.DisposeAsync();
            mount.Dispose();
            throw;
        }
    }
}
