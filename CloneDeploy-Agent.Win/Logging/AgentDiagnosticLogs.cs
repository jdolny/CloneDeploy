using System.Security.AccessControl;
using System.Security.Principal;

namespace CloneDeploy_Agent;

// Diagnostics are deliberately outside the SYSTEM-only identity/state directory.
internal static class AgentDiagnosticLogs
{
    internal static string DirectoryFor(string stateDirectory)
    {
        var state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        if (state == Path.GetPathRoot(state)) throw new IOException("Invalid agent state directory.");
        return state + "Logs";
    }

    internal static string FinalizationLog(string stateDirectory) => Path.Combine(DirectoryFor(stateDirectory), "image-prep-finalization.log");

    internal static void Prepare(string stateDirectory, SecurityIdentifier? additionalReader = null)
    {
        var directory = DirectoryFor(stateDirectory);
        // Checks trusted ownership, ancestors and reparse points before modifying permissions.
        AgentStatePermissions.Protect(directory);
        using var identity = WindowsIdentity.GetCurrent();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Apply(new DirectoryInfo(directory));
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo) throw new IOException("Unexpected directory in agent diagnostic logs.");
            Apply(entry);
        }

        void RequireTrusted(FileSystemInfo entry)
        {
            PolicyActionRunner.RejectReparsePoints(entry.FullName);
            var owner = entry is DirectoryInfo d ? d.GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                : ((FileInfo)entry).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner != identity.User && owner != system && owner != administrators)
                throw new UnauthorizedAccessException("Agent diagnostic logs have an untrusted owner.");
        }

        void Apply(FileSystemInfo entry)
        {
            RequireTrusted(entry);
            var inheritance = entry is DirectoryInfo ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            FileSystemSecurity acl = entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (identity.User != system)
                acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (additionalReader is not null)
                acl.AddAccessRule(new FileSystemAccessRule(additionalReader, FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
            if (entry is DirectoryInfo folder) folder.SetAccessControl((DirectorySecurity)acl);
            else ((FileInfo)entry).SetAccessControl((FileSecurity)acl);
        }
    }
}
