using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CloneDeploy_Imager.Diagnostics;
using Fsp;
using FsInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace CloneDeploy_Imager.FfuFileSystem.Reading;

// One immutable file. No writes, creates, deletes, renames, or reparse points.
/// <summary>
/// Exposes one immutable FFU file through WinFsp using the HTTPS reader for file contents.
/// </summary>
internal sealed class FfuReadFileSystem(HttpsFfuReader reader, DiagnosticLog log, CancellationToken stop) : FileSystemBase
{
    private const int FileInfoCacheMilliseconds = 1000;
    private const ulong HeaderDiagnosticBytes = 16UL * 1024 * 1024;
    private const int MaximumHeaderDiagnosticEntries = 64;

    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileDeleteOnClose = 0x00001000;
    private const ushort SectorBytes = 512;
    private const ushort SectorsPerAllocationUnit = 8;
    private const ushort MaximumFileNameCharacters = 255;
    private const ulong AllocationUnitBytes = SectorBytes * SectorsPerAllocationUnit;
    private const uint MutatingAccessRights = (uint)(FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership);

    private readonly ulong timestamp = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    private readonly byte[] security = Security();
    private readonly string networkPrefix = @"\CloneDeployHttps\" + Guid.NewGuid().ToString("N");
    private int headerReadLogs;
    private static byte[] Security()
    {
        // Stored ACEs must contain mapped file rights. Generic GR/GX bits are
        // not expanded in ACEs during access checks and can deny ordinary reads.
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot identify the filesystem owner.");
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:P(A;;FRFX;;;SY)(A;;FRFX;;;BA)(A;;FRFX;;;{sid})");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
    private static bool Exists(string name) => name == "\\" || name.Equals("\\image.ffu", StringComparison.OrdinalIgnoreCase);
    private FsInfo Info(bool directory) => new()
    {
        FileAttributes = (uint)(FileAttributes.ReadOnly | (directory ? FileAttributes.Directory : FileAttributes.Archive)),
        FileSize = directory ? 0 : (ulong)reader.Length,
        AllocationSize = directory ? 0 : ((ulong)reader.Length + AllocationUnitBytes - 1) / AllocationUnitBytes * AllocationUnitBytes,
        CreationTime = timestamp, LastAccessTime = timestamp, LastWriteTime = timestamp, ChangeTime = timestamp,
        IndexNumber = directory ? 1UL : 2UL, HardLinks = 1
    };
    public override int Init(object host)
    {
        var fs = (FileSystemHost)host;
        fs.SectorSize = SectorBytes; fs.SectorsPerAllocationUnit = SectorsPerAllocationUnit; fs.MaxComponentLength = MaximumFileNameCharacters;
        fs.CaseSensitiveSearch = false; fs.CasePreservedNames = true; fs.UnicodeOnDisk = true; fs.PersistentAcls = true;
        fs.FileInfoTimeout = FileInfoCacheMilliseconds; fs.FileSystemName = "CloneDeployFFU";
        // This source has no underlying local disk. A network prefix makes
        // Windows resolve its final path as UNC, including during DISM's
        // check that the source file is not on the disk being overwritten.
        // WinFsp serves this namespace locally; all content still uses HTTPS.
        fs.Prefix = networkPrefix;
        return STATUS_SUCCESS;
    }
    public override int GetVolumeInfo(out VolumeInfo info)
    { info = new() { TotalSize = (ulong)reader.Length, FreeSize = 0 }; info.SetVolumeLabel("HTTPS FFU image"); return STATUS_SUCCESS; }
    public override int GetSecurityByName(string name, out uint attributes, ref byte[] descriptor)
    {
        attributes = 0;
        if (!Exists(name)) return STATUS_OBJECT_NAME_NOT_FOUND;
        attributes = Info(name == "\\").FileAttributes;
        log.Write("WinFsp security lookup: " + (name == "\\" ? "root" : "image.ffu"));
        if (descriptor is not null) descriptor = security;
        return STATUS_SUCCESS;
    }
    public override int Open(string name, uint options, uint access, out object node, out object desc, out FsInfo info, out string normalized)
    {
        node = null!; desc = null!; info = default; normalized = name;
        if (!Exists(name)) return STATUS_OBJECT_NAME_NOT_FOUND;
        log.Write($"WinFsp open {(name == "\\" ? "root" : "image.ffu")}: access=0x{access:X8}, options=0x{options:X8}.");
        if ((access & MutatingAccessRights) != 0 || (options & FileDeleteOnClose) != 0) return STATUS_ACCESS_DENIED;
        var directory = name == "\\";
        if ((options & FileDirectoryFile) != 0 && !directory) return STATUS_NOT_A_DIRECTORY;
        if ((options & FileNonDirectoryFile) != 0 && directory) return STATUS_FILE_IS_A_DIRECTORY;
        node = directory; info = Info(directory); normalized = directory ? "\\" : "\\image.ffu";
        return STATUS_SUCCESS;
    }
    public override int GetFileInfo(object node, object desc, out FsInfo info)
    { info = Info((bool)node); return STATUS_SUCCESS; }
    public override int GetSecurity(object node, object desc, ref byte[] descriptor)
    { descriptor = security; return STATUS_SUCCESS; }
    public override int Flush(object node, object desc, out FsInfo info)
    { info = node is bool directory ? Info(directory) : default; return STATUS_SUCCESS; }
    public override bool ReadDirectoryEntry(object node, object desc, string pattern, string marker, ref object context,
        out string name, out FsInfo info)
    {
        name = ""; info = default;
        if (!(bool)node || context is not null || (!string.IsNullOrEmpty(marker) && string.Compare(marker, "image.ffu", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        context = true; name = "image.ffu"; info = Info(false); return true;
    }
    public override int Read(object node, object desc, IntPtr buffer, ulong offset, uint length, out uint transferred)
    {
        transferred = 0;
        if ((bool)node) return STATUS_FILE_IS_A_DIRECTORY;
        if (offset >= (ulong)reader.Length) return STATUS_END_OF_FILE;
        if (length == 0) return STATUS_SUCCESS;
        try
        {
            // Fill the native request up to EOF, while keeping our intermediate
            // allocation bounded. DISM need not retry a short successful read.
            var wanted = (uint)Math.Min(length, (ulong)reader.Length - offset);
            var bytes = new byte[(int)Math.Min(wanted, HttpsFfuReader.BlockSize)];
            uint copied = 0;
            while (copied < wanted)
            {
                var chunk = (int)Math.Min(wanted - copied, (uint)bytes.Length);
                var count = reader.ReadAsync((long)(offset + copied), bytes.AsMemory(0, chunk), stop).GetAwaiter().GetResult();
                if (count != chunk) throw new EndOfStreamException("FFU range ended before its published size.");
                Marshal.Copy(bytes, 0, (IntPtr)(buffer.ToInt64() + copied), count);
                copied += (uint)count;
            }
            transferred = copied;
            if (offset < HeaderDiagnosticBytes && Interlocked.Increment(ref headerReadLogs) <= MaximumHeaderDiagnosticEntries)
                log.Write($"WinFsp FFU header read: offset={offset}, requested={length}, returned={transferred}.");
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { log.Failure("WinFsp HTTPS read", ex); return STATUS_UNEXPECTED_IO_ERROR; }
    }
    public override int ExceptionHandler(Exception ex)
    { log.Failure("WinFsp filesystem", ex); return STATUS_UNEXPECTED_IO_ERROR; }
}
