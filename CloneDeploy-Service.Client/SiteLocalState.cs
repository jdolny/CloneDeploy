using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CloneDeploy.SiteServices;
using Microsoft.AspNetCore.DataProtection;

namespace CloneDeploy.SiteService;

public sealed record SiteLocalState(SiteEnrollmentPackage Package, string IdentityKey, string HttpsKey,
    string IdentityCsr, string HttpsCsr, string PfxPassword, SiteConfiguration? Configuration);

public sealed class SiteLocalStore
{
    public string DirectoryPath { get; }
    public string StatePath => Path.Combine(DirectoryPath, "enrollment.protected");
    private readonly IDataProtector protector;
    public bool? ExpectedWindowsWorker { get; }
    public SiteLocalStore(string directory, bool expectedWindowsWorker) : this(directory) => ExpectedWindowsWorker = expectedWindowsWorker;
    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "SiteService")
        : "/var/lib/clonedeploy/siteservice";

    public SiteLocalStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (Path.TrimEndingDirectorySeparator(DirectoryPath) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(DirectoryPath)!))
            throw new InvalidDataException("Use a dedicated SiteService data directory.");
        var marker = Path.Combine(DirectoryPath, "siteservice.directory");
        if (Directory.Exists(DirectoryPath) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any(entry => Path.GetFileName(entry) != "siteservice.json" || !File.Exists(entry)))
            throw new InvalidDataException("Use an empty directory for a new SiteService installation.");
        var info = Directory.CreateDirectory(DirectoryPath);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(marker, "CloneDeploy-SiteService v1");
        protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DirectoryPath, "keys")), builder =>
        {
            builder.SetApplicationName("CloneDeploy-SiteService");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }).CreateProtector("Enrollment.v1");
    }

    public SiteLocalState Load() => JsonSerializer.Deserialize<SiteLocalState>(protector.Unprotect(File.ReadAllText(StatePath)))
        ?? throw new InvalidDataException("Invalid enrollment state.");
    public void Save(SiteLocalState state, bool create = false)
    {
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(state)));
            File.Move(temporary, StatePath, overwrite: !create);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
