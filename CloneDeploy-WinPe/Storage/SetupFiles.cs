using System.Text.RegularExpressions;

namespace CloneDeploy_Server.WinPe;

/// <summary>
/// Locates builder setup files and reads setup progress.
/// </summary>
public static class SetupFiles
{
    public static string DirectoryPath(BuildStorage storage, Guid id) => Path.Combine(storage.Root, "setup-" + id.ToString("N"));
    public static string PathFor(BuildStorage storage, Guid id, string name)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..") throw new InvalidDataException("Invalid setup file name.");
        return Path.Combine(DirectoryPath(storage, id), name);
    }
    public static SetupStatusDto Status(BuildStorage storage, Guid id) => storage.Read<SetupStatusDto>(PathFor(storage, id, "status.json")) ?? new("Queued", "Waiting for the Windows builder.", DateTime.UtcNow);
}
