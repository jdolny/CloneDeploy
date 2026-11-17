using System.IO.Compression;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CloneDeploy_Server.WinPe;

// Shared local spool. Only the server account and the elevated builder account may write here.
/// <summary>
/// Stores boot image build requests, progress, and working files.
/// </summary>
public sealed class BuildStorage(string root)
{
    public bool SecureWrites { get; init; }
    public string? QueueWriterSid { get; init; }
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumExpandedArchiveBytes = 40L * 1024 * 1024 * 1024;
    private const uint WimReparseTag = 0x80000008u;
    private const uint WindowsOverlayReparseTag = 0x80000017u;
    private const int FileAttributeTagInformationClass = 9;

    public string Root { get; } = Path.GetFullPath(root);
    public string Job(Guid id) => Path.Combine(Root, id.ToString("N"));
    public string FilePath(Guid id, string name)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            throw new InvalidDataException("Invalid build file name.");
        return Path.Combine(Job(id), name);
    }
    public async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        if (SecureWrites && OperatingSystem.IsWindows())
            await BuilderSecurity.AsQueueWriterAsync(QueueWriterSid, () => WriteCoreAsync(path, value, ct));
        else await WriteCoreAsync(path, value, ct);
    }
    private async Task WriteCoreAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (SecureWrites && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected queue writes require Windows.");
        using var lease = SecureWrites && OperatingSystem.IsWindows() ? BuilderSecurity.PinDirectory(Path.GetDirectoryName(path)!) : null;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, value, cancellationToken: ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public T? Read<T>(string path)
    {
        try
        {
            if (SecureWrites && OperatingSystem.IsWindows())
            {
                using var directory = BuilderSecurity.PinDirectory(Path.GetDirectoryName(path)!);
                using var input = BuilderSecurity.OpenInput(path);
                if (input.Length > 1024 * 1024) throw new InvalidDataException("WinPE queue metadata exceeds 1 MB.");
                return JsonSerializer.Deserialize<T>(input);
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (IOException) { return default; }
        catch (InvalidDataException) { return default; }
        catch (JsonException) { return default; }
    }
    public BuildStatusDto Status(Guid id) => Read<BuildStatusDto>(FilePath(id, "status.json"))
        ?? new("Queued", "Waiting for the Windows builder.", DateTime.UtcNow, []);
    public Task SetStatus(Guid id, string state, string message, List<BuildArtifactDto>? artifacts = null) =>
        WriteAsync(FilePath(id, "status.json"), new BuildStatusDto(state, message, DateTime.UtcNow, artifacts ?? []));
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            var tag = OperatingSystem.IsWindows() ? ReparseTag(current) : 0u;
            RejectUnsupportedReparseTag(current, tag);
        }
    }

    public static void RejectUnsupportedReparseTag(string path, uint tag)
    {
        // WIMMount and Windows Overlay backing are not path aliases. Mounted WinPE files
        // legitimately use these tags. Still reject junctions, symlinks and unknown tags.
        if (tag is WimReparseTag or WindowsOverlayReparseTag) return;
        throw new InvalidDataException($"Build paths cannot contain links or unsupported reparse points: {path} (tag 0x{tag:X8}).");
    }

    private static uint ReparseTag(string path)
    {
        const uint openExisting = 3, openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        // Query the object itself, without following a final symlink; directories need backup semantics.
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileAttributeTagInformationClass, out var information, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new IOException("Could not inspect build reparse point: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        return information.ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo information, uint bufferSize);

    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        RejectLinks(destination);
        Directory.CreateDirectory(destination);
        var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long size = 0;
        if (zip.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("Archive contains too many files.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            size = checked(size + entry.Length);
            if (size > MaximumExpandedArchiveBytes) throw new InvalidDataException("Archive exceeds the 40 GB expanded limit.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('\\', '/')));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':'))
                throw new InvalidDataException("Archive entry escapes the build directory.");
            RejectLinks(target);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew);
            await input.CopyToAsync(output, ct);
        }
    }
}
