using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CloneDeploy_Imager.Diagnostics;
using Fsp;
using FsInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace CloneDeploy_Imager.FfuFileSystem.Writing;

// Only one mutable, unfinished upload. Rename/delete/reparse operations are unsupported.
/// <summary>
/// Exposes a single writable FFU capture file through WinFsp and reports callback failures.
/// </summary>
internal sealed class FfuCaptureFileSystem(BufferedFfuCaptureFile file, DiagnosticLog log) : FileSystemBase
{
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileDeleteOnClose = 0x00001000;
    private const ushort SectorBytes = 512;
    private const ushort SectorsPerAllocationUnit = 8;
    private const ushort MaximumFileNameCharacters = 255;
    private const ulong AllocationUnitBytes = SectorBytes * SectorsPerAllocationUnit;

    internal const long Maximum = 2L * 1024 * 1024 * 1024 * 1024;
    private const int Chunk = 8 * 1024 * 1024;
    private readonly ulong timestamp = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    private readonly byte[] security = Security();
    private Exception? failure;
    public void EnsureHealthy() { if (failure is not null) throw new IOException("HTTPS FFU filesystem failed; capture cannot be published.", failure); }
    private static byte[] Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{identity.User!.Value})");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
    private static bool Target(string name) => name.Equals(@"\capture.ffu", StringComparison.OrdinalIgnoreCase);
    private bool Exists(string name) => name == @"\" || Target(name) && file.Exists;
    private FsInfo Info(bool directory) => new() { FileAttributes = (uint)(directory ? FileAttributes.Directory : FileAttributes.Archive),
        FileSize = directory ? 0 : (ulong)file.Length, AllocationSize = directory ? 0 : ((ulong)file.Length + AllocationUnitBytes - 1) / AllocationUnitBytes * AllocationUnitBytes,
        CreationTime = timestamp, LastWriteTime = timestamp, LastAccessTime = timestamp, ChangeTime = timestamp, IndexNumber = directory ? 1UL : 2UL, HardLinks = 1 };
    public override int Init(object host)
    {
        var fs = (FileSystemHost)host;
        fs.SectorSize = SectorBytes; fs.SectorsPerAllocationUnit = SectorsPerAllocationUnit; fs.MaxComponentLength = MaximumFileNameCharacters;
        fs.CaseSensitiveSearch = false; fs.CasePreservedNames = true; fs.UnicodeOnDisk = true; fs.PersistentAcls = true;
        fs.FileInfoTimeout = 0; fs.FileSystemName = "CloneDeployFFU";
        fs.Prefix = @"\CloneDeployUpload\" + Guid.NewGuid().ToString("N");
        return STATUS_SUCCESS;
    }
    public override int GetVolumeInfo(out VolumeInfo info)
    { info = new() { TotalSize = Maximum, FreeSize = (ulong)(Maximum - file.Length) }; info.SetVolumeLabel("HTTPS FFU capture"); return STATUS_SUCCESS; }
    public override int GetSecurityByName(string name, out uint attributes, ref byte[] descriptor)
    {
        attributes = 0; if (!Exists(name)) return STATUS_OBJECT_NAME_NOT_FOUND;
        attributes = Info(name == @"\").FileAttributes; if (descriptor is not null) descriptor = security; return STATUS_SUCCESS;
    }
    public override int Open(string name, uint options, uint access, out object node, out object desc, out FsInfo info, out string normalized)
    {
        node = null!; desc = null!; info = default; normalized = name;
        if (!Exists(name)) return STATUS_OBJECT_NAME_NOT_FOUND;
        if ((options & FileDeleteOnClose) != 0) return STATUS_ACCESS_DENIED;
        var dir = name == @"\";
        if (!dir && (options & FileDirectoryFile) != 0) return STATUS_NOT_A_DIRECTORY;
        if (dir && (options & FileNonDirectoryFile) != 0) return STATUS_FILE_IS_A_DIRECTORY;
        node = dir; info = Info(dir); return STATUS_SUCCESS;
    }
    public override int Create(string name, uint options, uint access, uint attributes, byte[] descriptor, ulong allocation,
        out object node, out object desc, out FsInfo info, out string normalized)
    {
        node = null!; desc = null!; info = default; normalized = name;
        if (!Target(name) || (options & (FileDirectoryFile | FileDeleteOnClose)) != 0 || allocation > Maximum) return STATUS_ACCESS_DENIED;
        if (file.Exists) return STATUS_OBJECT_NAME_COLLISION;
        file.Create(); node = false; info = Info(false); return STATUS_SUCCESS;
    }
    public override int Overwrite(object node, object desc, uint attributes, bool replace, ulong allocation, out FsInfo info)
    { info = default; if ((bool)node || allocation > Maximum) return STATUS_ACCESS_DENIED; file.SetLength(0); info = Info(false); return STATUS_SUCCESS; }
    public override int GetFileInfo(object node, object desc, out FsInfo info) { info = Info((bool)node); return STATUS_SUCCESS; }
    public override int GetSecurity(object node, object desc, ref byte[] descriptor) { descriptor = security; return STATUS_SUCCESS; }
    public override int SetBasicInfo(object node, object desc, uint attributes, ulong creation, ulong access, ulong write, ulong change, out FsInfo info)
    { info = Info((bool)node); return STATUS_SUCCESS; }
    public override int SetFileSize(object node, object desc, ulong size, bool allocation, out FsInfo info)
    {
        info = default; if ((bool)node || size > Maximum) return STATUS_ACCESS_DENIED;
        if (!allocation || size < (ulong)file.Length) file.SetLength((long)size);
        info = Info(false); return STATUS_SUCCESS;
    }
    public override int Flush(object node, object desc, out FsInfo info)
    { file.Flush(); info = Info(node is bool directory && directory); return STATUS_SUCCESS; }
    public override int Read(object node, object desc, IntPtr buffer, ulong offset, uint length, out uint transferred)
    {
        transferred = 0; if ((bool)node) return STATUS_FILE_IS_A_DIRECTORY;
        if (length == 0) return STATUS_SUCCESS;
        if (offset >= (ulong)file.Length) return STATUS_END_OF_FILE;
        var wanted = (uint)Math.Min(length, (ulong)file.Length - offset);
        while (transferred < wanted)
        {
            var count = (int)Math.Min(Chunk, wanted - transferred);
            var bytes = file.Read((long)(offset + transferred), count);
            if (bytes.Length != count) throw new EndOfStreamException();
            Marshal.Copy(bytes, 0, (IntPtr)(buffer.ToInt64() + transferred), count); transferred += (uint)count;
        }
        return STATUS_SUCCESS;
    }
    public override int Write(object node, object desc, IntPtr buffer, ulong offset, uint length, bool append, bool constrained, out uint transferred, out FsInfo info)
    {
        transferred = 0; info = default; if ((bool)node) return STATUS_FILE_IS_A_DIRECTORY;
        if (append) offset = (ulong)file.Length;
        if (offset > Maximum || length > (ulong)Maximum - offset) return STATUS_DISK_FULL;
        var wanted = constrained ? (uint)Math.Min(length, offset >= (ulong)file.Length ? 0 : (ulong)file.Length - offset) : length;
        while (transferred < wanted)
        {
            var count = (int)Math.Min(Chunk, wanted - transferred);
            var bytes = new byte[count]; Marshal.Copy((IntPtr)(buffer.ToInt64() + transferred), bytes, 0, count);
            file.Write((long)(offset + transferred), bytes); transferred += (uint)count;
        }
        info = Info(false); return STATUS_SUCCESS;
    }
    public override bool ReadDirectoryEntry(object node, object desc, string pattern, string marker, ref object context, out string name, out FsInfo info)
    {
        name = ""; info = default;
        if (!(bool)node || !file.Exists || context is not null || !string.IsNullOrEmpty(marker) && string.Compare(marker, "capture.ffu", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        context = true; name = "capture.ffu"; info = Info(false); return true;
    }
    public override int ExceptionHandler(Exception ex) { failure ??= ex; log.Failure("HTTPS FFU capture filesystem", ex); return STATUS_UNEXPECTED_IO_ERROR; }
}
