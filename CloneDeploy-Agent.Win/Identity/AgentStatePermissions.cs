using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent;

internal static class AgentStatePermissions
{
    internal static void Protect(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var administratorMembership = new Dictionary<string, bool>();
        bool Trusted(IdentityReference? sid)
        {
            if (sid is null) return false;
            if (sid == current || sid == system || sid == administrators) return true;
            // Explorer's elevated "Continue" prompt grants the individual administrator
            // access. Treat verified local administrators like the Administrators group.
            // Cache only for this operation so membership changes are not retained.
            if (!administratorMembership.TryGetValue(sid.Value, out var trusted))
                administratorMembership[sid.Value] = trusted = sid is SecurityIdentifier account && IsLocalAdministrator(account);
            return trusted;
        }

        // Check ancestors before CreateDirectory or opening any state. Never follow a junction.
        for (var ancestor = new DirectoryInfo(Path.GetFullPath(path)); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Exists) continue;
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Agent state cannot be stored beneath a reparse point.");
            // ProgramData permits users to create directories. An untrusted owner of a
            // pre-created CloneDeploy parent must not retain the ability to replace Agent.
            if (identity.IsSystem && ancestor.Name.Equals("CloneDeploy", StringComparison.OrdinalIgnoreCase) &&
                !Trusted(ancestor.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("The agent state parent has an untrusted owner.");
            if (identity.IsSystem)
            {
                var dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                foreach (FileSystemAccessRule rule in ancestor.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow &&
                        (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                        (rule.FileSystemRights & dangerous) != 0 && !Trusted(rule.IdentityReference) &&
                        rule.IdentityReference.Value != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // TrustedInstaller
                        throw new UnauthorizedAccessException($"Agent state ancestor '{ancestor.FullName}' grants '{rule.IdentityReference.Value}' untrusted rights: {rule.FileSystemRights}.");
            }
        }

        var root = Directory.CreateDirectory(path);
        ProtectEntry(root);
        void ProtectEntry(FileSystemInfo entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Agent state contains a reparse point.");
            FileSystemSecurity original = entry is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
            if (!Trusted(original.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("Agent state has an untrusted owner.");
            var inheritance = entry is DirectoryInfo ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            FileSystemSecurity security = entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (current != system)
                security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (entry is DirectoryInfo directory)
            {
                directory.SetAccessControl((DirectorySecurity)security);
                foreach (var child in directory.EnumerateFileSystemInfos())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Agent state contains a reparse point.");
                    // Root files contain configuration, CA and identity state. Subfolders
                    // (for example logs) have separate purpose-specific access policies.
                    if (child is FileInfo) ProtectEntry(child);
                }
            }
            else ((FileInfo)entry).SetAccessControl((FileSecurity)security);
        }
    }

    internal static bool IsLocalAdministrator(SecurityIdentifier sid)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var account = (NTAccount)sid.Translate(typeof(NTAccount));
            var administrators = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount));
            var groupName = administrators.Value[(administrators.Value.IndexOf('\\') + 1)..];
            // Query this computer, including membership through domain global groups.
            // A failed/incomplete lookup must never establish trust.
            if (NetUserGetLocalGroups(null, account.Value, 0, 1, out buffer, -1, out var count, out _) != 0)
                return false;
            for (var i = 0; i < count; i++)
                if (string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, checked(i * IntPtr.Size))),
                        groupName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch (IdentityNotMappedException) { return false; }
        catch (SystemException ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? servername, string username, int level, int flags,
        out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
