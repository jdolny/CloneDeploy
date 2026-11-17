using System.ComponentModel;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Services;

namespace CloneDeploy.SiteService;

// Connections belong to the service logon session, never to an interactive drive letter.
internal static class SiteStorageConnection
{
    private const int ProbeBufferBytes = 4096;
    private const byte ProbeByte = 0x43;

    public static async Task CheckAsync(SiteStorageProvider provider, Guid site, CancellationToken ct)
    {
        var target = provider.Path;
        if (OperatingSystem.IsLinux())
        {
            LinuxSmbMount.Verify(provider.LinuxMountPath, provider.Path);
            target = provider.LinuxMountPath!;
        }
        else
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("SMB storage supports Windows connections or Linux OS-managed mounts.");
            if (provider.LinuxMountPath is not null) throw new IOException("Linux mount configuration cannot be used on a Windows host.");
            if (!provider.Path.StartsWith(@"\\") || provider.Path.StartsWith(@"\\?\") || provider.Path.StartsWith(@"\\.\") ||
                provider.Path[2..].Split('\\').Length < 2 || provider.Path[2..].Split('\\').Take(2).Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid SMB share path.");
            try
            {
                await Task.Run(() => WindowsStorageLinks.ConnectShare(provider.Path, provider.Username, provider.Password), ct);
            }
            catch (Win32Exception ex) { throw new IOException($"SMB connection failed (Windows error {ex.NativeErrorCode})."); }
            catch (InvalidOperationException) { throw new IOException("SMB connection conflicts with another credential in the service logon session. Use matching credentials or restart the service after updating them."); }
        }
        ct.ThrowIfCancellationRequested();
        using (var entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator()) _ = entries.MoveNext();
        var sample = SiteStorageProtocol.Resolve(target, site, provider.Id, "image-" + Guid.Empty.ToString("N"), OperatingSystem.IsWindows());
        // Probe only our provider namespace; keep temporary checks out of assigned replica folders.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(sample))!;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $".probe-{Guid.NewGuid():N}");
        await using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ProbeBufferBytes,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await probe.WriteAsync(new byte[] { ProbeByte }, ct);
        await probe.FlushAsync(ct);
        probe.Position = 0;
        var bytes = new byte[1];
        if (await probe.ReadAsync(bytes, ct) != 1 || bytes[0] != ProbeByte) throw new IOException("SMB read/write check failed.");
    }
}
