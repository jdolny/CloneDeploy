using CloneDeploy_Imager.Deployment;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Discovery;

namespace CloneDeploy_Imager;

internal static class CaptureAnswerFileCleanup
{
    internal static string[] Paths(string root, string? reference)
    {
        var files = new List<string>();
        foreach (var folder in new[] { "", @"Windows\Panther", @"Windows\Panther\Unattend", @"Windows\System32\Sysprep" })
            foreach (var name in new[] { "unattend.xml", "autounattend.xml" })
                files.Add(Path.Combine(root, folder, name));
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var path = reference.Trim().Trim('"')
                .Replace("%WINDIR%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
                .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
                .Replace("%SystemDrive%", "C:", StringComparison.OrdinalIgnoreCase);
            if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('%'))
                throw new IOException("The source answer-file reference is not a supported local path.");
            files.Add(Path.Combine(root, path[3..]));
        }
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = files.SelectMany(x => new[] { x, x + ".clonedeploy-backup", x + ".bak" })
            .Append(Path.Combine(root, @"Windows\Setup\Scripts\CloneDeploy-SysprepCredentials.ticket"))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in result)
        {
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Answer-file cleanup path escapes the source volume.");
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Answer-file cleanup cannot follow file or directory links.");
        }
        return result;
    }

    internal static void Clear(WindowsVolume source, DiagnosticLog log)
    {
        if (source.RunningWindows) throw new IOException("Capture cleanup requires offline Windows.");
        OfflineComputerName.WithLoadedHive(Path.Combine(source.Root, @"Windows\System32\config\SYSTEM"), true, log, hive =>
        {
            using var setup = hive.OpenSubKey("Setup", writable: true);
            var files = Paths(source.Root, setup?.GetValue("UnattendFile") as string);
            foreach (var file in files)
                if (Path.Exists(file)) File.Delete(file);
            setup?.DeleteValue("UnattendFile", throwOnMissingValue: false);
            setup?.Flush();
        });
        log.Write("Removed source Windows answer files, backups, credential ticket and setup answer-file reference after successful capture.");
    }
}
