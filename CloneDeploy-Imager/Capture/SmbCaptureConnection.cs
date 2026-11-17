using System.ComponentModel;
using System.Runtime.InteropServices;
using CloneDeploy_Imager.Api;

namespace CloneDeploy_Imager.Imaging;

/// <summary>
/// Owns an SMB connection used for capture and releases that connection when capture ends.
/// </summary>
internal sealed class SmbCaptureConnection : IDisposable
{
    private const int MaximumSharePathCharacters = 2048;

    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSessionCredentialConflict = 1219;

    private readonly string drive;
    public string FilePath { get; }
    private SmbCaptureConnection(string drive, string filePath) { this.drive = drive; FilePath = filePath; }

    public static void Validate(SmbCapture destination)
    {
        if (!destination.Root.StartsWith(@"\\", StringComparison.Ordinal) || destination.Root.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            destination.Root.Length > MaximumSharePathCharacters || destination.Root[2..].Split('\\').Where(x => x.Length > 0).Count() < 2 ||
            destination.Root[2..].Split('\\').Any(x => x is "." or ".." || x.IndexOfAny([':', '/', '*', '?', '"']) >= 0 || x.Any(char.IsControl)) ||
            Path.IsPathRooted(destination.RelativePath) || destination.RelativePath.Split('\\').Any(x => x is "" or "." or ".." || x.IndexOfAny([':', '/', '*', '?', '"']) >= 0 || x.Any(char.IsControl)))
            throw new InvalidDataException("Invalid direct SMB capture destination.");
    }

    public static SmbCaptureConnection Connect(SmbCapture destination, string username, string password)
    {
        Validate(destination);
        var parts = destination.Root.TrimEnd('\\')[2..].Split('\\');
        var share = $@"\\{parts[0]}\{parts[1]}";
        var used = Environment.GetLogicalDrives().Select(x => char.ToUpperInvariant(x[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (used.Contains(letter)) continue;
            var drive = letter + ":";
            var resource = new NetworkResource { Type = ResourceTypeDisk, LocalName = drive, RemoteName = share };
            var error = WNetAddConnection2(ref resource, password, username, ConnectTemporary);
            if (error == ErrorAlreadyAssigned) continue; // Another process claimed the letter.
            if (error == ErrorSessionCredentialConflict) throw new InvalidOperationException("An existing SMB connection uses different credentials. Disconnect it or use the same account.");
            if (error != 0) throw new InvalidOperationException($"SMB connection failed (Windows error {error}): {new Win32Exception(error).Message}");
            var suffix = string.Join('\\', parts.Skip(2).Append(destination.RelativePath));
            return new(drive, drive + @"\" + suffix);
        }
        throw new InvalidOperationException("No free drive letter is available for the SMB capture connection.");
    }

    public void Dispose() => WNetCancelConnection2(drive, 0, false);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource { public int Scope, Type, DisplayType, Usage; public string? LocalName, RemoteName, Comment, Provider; }
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetworkResource resource, string? password, string? username, int flags);
    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}
