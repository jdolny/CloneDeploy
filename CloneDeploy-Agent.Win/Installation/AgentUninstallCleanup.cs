using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;

namespace CloneDeploy_Agent;

internal static class AgentUninstallCleanup
{
    internal static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
            throw new UnauthorizedAccessException("Agent data cleanup requires SYSTEM.");
        // MSI schedules this after StopServices and before RemoveFiles. Never purge a running agent.
        using var service = new ServiceController(AgentOptions.WindowsServiceName);
        if (service.Status != ServiceControllerStatus.Stopped)
            throw new InvalidOperationException("Stop the installed agent before removing its data.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Agent");
        var logs = AgentDiagnosticLogs.DirectoryFor(directory);
        PolicyActionRunner.RejectReparsePoints(logs);
        if (Directory.Exists(logs)) ValidateTree(logs);
        RemoveState(directory);
        if (Directory.Exists(logs)) DeleteTree(logs);
    }

    // Separate from the command entry point so tests can exercise disposable state without touching the installation.
    internal static void RemoveState(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (directory == Path.GetPathRoot(directory)) throw new IOException("Cannot remove a drive root.");
        PolicyActionRunner.RejectReparsePoints(directory);
        if (!Directory.Exists(directory)) return;
        ValidateTree(directory);
        // Hold the same exclusive lock as the service while reading and removing its named CNG key.
        using (var store = new AgentStore(directory))
        {
            var state = store.LoadIdentity();
            if (state is not null)
            {
                foreach (var name in new[] { state.KeyName, state.PendingKeyName, state.PreviousKeyName })
                {
                    if (name is null) continue;
                    const string prefix = "CloneDeploy.Agent.";
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                        !Guid.TryParseExact(name[prefix.Length..], "N", out _))
                        throw new InvalidDataException("Cannot remove an unrecognized agent private key.");
                    AgentStore.DeleteIdentityKey(name);
                }
            }
        }
        DeleteTree(directory);
    }

    private static void ValidateTree(string directory)
    {
        PolicyActionRunner.RejectReparsePoints(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PolicyActionRunner.RejectReparsePoints(path);
            if (Directory.Exists(path)) ValidateTree(path);
        }
    }

    private static void DeleteTree(string directory)
    {
        PolicyActionRunner.RejectReparsePoints(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PolicyActionRunner.RejectReparsePoints(path);
            if (Directory.Exists(path)) DeleteTree(path);
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        Directory.Delete(directory); // Nonrecursive; each descendant was checked before deletion.
    }
}
