using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

namespace CloneDeploy_Server.WinPe;

/// <summary>Windows permission checks and non-following handles at the privileged queue boundary.</summary>
[SupportedOSPlatform("windows")]
public static class BuilderSecurity
{
    private const string SystemSid = "S-1-5-18", AdministratorsSid = "S-1-5-32-544";

    public static void ValidateAcl(FileSystemSecurity acl, string path, string? queueWriterSid = null, bool ancestor = false)
    {
        // Administrators manage ownership and access grants. Do not reject additional SIDs,
        // including individual accounts granted access when browsing build logs.
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count == 0) throw new InvalidOperationException("Missing WinPE access restrictions: " + path);
    }

    public static void ValidateDirectory(string path, string? queueWriterSid = null)
    {
        using var lease = PinDirectory(path);
        ValidateAcl(new DirectoryInfo(path).GetAccessControl(), path, queueWriterSid);
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null; parent = Path.GetDirectoryName(parent))
            ValidateAcl(new DirectoryInfo(parent).GetAccessControl(), parent, queueWriterSid, ancestor: true);
    }

    public static void ValidateInstallation(string path)
    {
        ValidateDirectory(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            BuildStorage.RejectLinks(entry);
            if (Directory.Exists(entry)) ValidateInstallation(entry);
            else ValidateAcl(new FileInfo(entry).GetAccessControl(), entry);
        }
    }

    public static void CreatePrivateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Invalid private WinPE root.");
            using var lease = PinDirectory(parent);
            // Require explicit access restrictions on the parent chain without a SID allowlist.
            for (var current = parent; current is not null; current = Path.GetDirectoryName(current))
                ValidateAcl(new DirectoryInfo(current).GetAccessControl(), current, ancestor: true);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(new SecurityIdentifier(AdministratorsSid));
            foreach (var sid in new[] { SystemSid, AdministratorsSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
        }
        // Preserve administrator-managed permissions on existing directories.
        ValidateDirectory(path);
    }

    public static IDisposable PinDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The privileged builder requires Windows.");
        if (Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Privileged WinPE paths must use local drive paths, not UNC/device paths.");
        var lease = new DirectoryLease();
        try
        {
            var ancestors = new Stack<string>();
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
            foreach (var directory in ancestors)
            {
                // A metadata-only (zero-access) handle does not enforce delete sharing on Windows.
                var handle = Open(directory, 0x80000000, FileShare.ReadWrite, 0x02000000);
                lease.Handles.Add(handle);
                var info = Information(handle, directory);
                if ((info.Attributes & (uint)FileAttributes.Directory) == 0) throw new IOException("Expected a directory: " + directory);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static FileStream OpenInput(string path)
    {
        var handle = Open(path, 0x80000000, FileShare.Read, 0x08000000);
        try
        {
            var info = Information(handle, path);
            if (info.NumberOfLinks != 1 || (info.Attributes & (uint)FileAttributes.Directory) != 0)
                throw new InvalidDataException("WinPE inputs must be regular files with a single link: " + path);
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    // Results go into a producer-writable tree. Restrict writes to that producer's permissions as
    // well as the builder's, so a last-moment path substitution cannot turn publication into a
    // SYSTEM write elsewhere. Private input handles are opened before entering this token.
    public static async Task AsQueueWriterAsync(string? sid, Func<Task> operation)
    {
        if (string.IsNullOrWhiteSpace(sid)) { await operation(); return; }
        var identifier = new SecurityIdentifier(sid);
        var bytes = new byte[identifier.BinaryLength]; identifier.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
            // DISABLE_MAX_PRIVILEGE | WRITE_RESTRICTED: reads/traversal still work, all writes
            // must also be authorized for the producer SID.
            if (!CreateRestrictedToken(identity.AccessToken, 9, 0, IntPtr.Zero, 0, IntPtr.Zero,
                    1, [new SidAndAttributes { Sid = memory }], out var restricted))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict WinPE result publication.");
            using (restricted) await WindowsIdentity.RunImpersonatedAsync(restricted, operation);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static SafeFileHandle Open(string path, uint access, FileShare share, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, 3, flags | 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException("Cannot securely open WinPE path: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static HandleInformation Information(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot inspect WinPE path: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("WinPE queue paths cannot contain reparse points: " + path);
        return info;
    }

    private sealed class DirectoryLease : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags, uint disabledSidCount,
        IntPtr disabledSids, uint deletedPrivilegeCount, IntPtr deletedPrivileges, uint restrictedSidCount,
        [In] SidAndAttributes[] restrictedSids, out SafeAccessTokenHandle restricted);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
}
