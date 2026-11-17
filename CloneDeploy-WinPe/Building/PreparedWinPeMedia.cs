using System.Security.Cryptography;
using System.Text;

namespace CloneDeploy_Server.WinPe;

// Disposable builder-local media, shared by variants but never customized in place.
public sealed class PreparedWinPeMedia(BuildStorage storage)
{
    private const int NativeErrorCodeMask = 0xffff;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int ResourceTemporarilyUnavailable = 11;
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(250);

    public static readonly string[] Components = ["WinPE-WMI", "WinPE-NetFx", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI", "WinPE-DismCmdlets"];

    public static string Key(string toolchainVersion, string architecture) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"media-v1|{toolchainVersion}|{architecture}|en-us|{string.Join(',', Components)}")));

    public async Task RestoreOrPrepareAsync(string toolchainVersion, string architecture, string destination,
        Func<string, Task> prepare, CancellationToken ct)
    {
        var directory = Path.Combine(storage.Root, "prepared-media", Key(toolchainVersion, architecture));
        BuildStorage.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "media.zip");
        var checksum = Path.Combine(directory, "sha256.txt");
        var lockPath = Path.Combine(directory, "prepare.lock");
        BuildStorage.RejectLinks(lockPath);
        await using var claim = await ClaimAsync(lockPath, ct);
        BuildStorage.RejectLinks(archive);
        BuildStorage.RejectLinks(checksum);
        if (File.Exists(archive) && File.Exists(checksum) &&
            await BuildStorage.HashAsync(archive, ct) == await File.ReadAllTextAsync(checksum, ct))
        {
            await BuildStorage.ExtractAsync(archive, destination, ct);
            return;
        }

        var pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // The callback prepares this job's working media and archives it before customization.
            await prepare(pending);
            var hash = await BuildStorage.HashAsync(pending, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(pending, archive, true);
            await File.WriteAllTextAsync(checksum, hash, ct);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    private static async Task<FileStream> ClaimAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & NativeErrorCodeMask) is SharingViolation or LockViolation or ResourceTemporarilyUnavailable) { await Task.Delay(LockRetryInterval, ct); }
        }
    }
}
