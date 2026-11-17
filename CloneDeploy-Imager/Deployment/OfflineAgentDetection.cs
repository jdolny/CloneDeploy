using System.Text.RegularExpressions;
using CloneDeploy_Imager.Diagnostics;
using Microsoft.Win32;

namespace CloneDeploy_Imager.Deployment;

internal static class OfflineAgentDetection
{
    private const int MaximumControlSetNumber = 999;

    private const string ServiceName = "CloneDeployAgent";

    public static bool IsInstalled(string windowsRoot, DiagnosticLog log)
    {
        var path = Path.Combine(windowsRoot, @"Windows\System32\config\SYSTEM");
        var installed = false;
        OfflineComputerName.WithLoadedHive(path, writable: false, log, hive =>
        {
            using var select = hive.OpenSubKey("Select");
            if (select?.GetValue("Default") is not int controlSet || controlSet is < 1 or > MaximumControlSetNumber)
                throw new InvalidDataException("The deployed Windows default control set is invalid.");
            using var services = hive.OpenSubKey($@"ControlSet{controlSet:000}\Services")
                ?? throw new InvalidDataException("The deployed Windows services registry is missing.");
            using var service = services.OpenSubKey(ServiceName);
            if (service is null) return;
            var imagePath = service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var executable = ExecutablePath(windowsRoot, imagePath);
            installed = File.Exists(executable);
        });
        return installed;
    }

    internal static string ExecutablePath(string windowsRoot, string? imagePath)
    {
        var match = Regex.Match(imagePath?.Trim() ?? "", "^\"?(.+?\\.exe)(?:\"|\\s|$)", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("The deployed agent service executable path is invalid.");
        var path = match.Groups[1].Value;
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        path = path.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
            .Replace("%SystemDrive%", "C:", StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase);
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('%'))
            throw new InvalidDataException("The deployed agent service must use a local Windows executable path.");
        var root = Path.GetFullPath(windowsRoot).TrimEnd('\\') + "\\";
        var result = Path.GetFullPath(Path.Combine(root, path[3..]));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The deployed agent executable path escapes the Windows volume.");
        return result;
    }

}
