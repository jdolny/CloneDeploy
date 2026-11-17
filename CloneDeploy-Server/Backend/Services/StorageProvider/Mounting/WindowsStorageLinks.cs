using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloneDeploy_Server.Backend.Services;

// Deviceless SMB connections; credentials stay in process and no drive letters are assigned.
/// <summary>
/// Connects to Windows network shares using saved credentials.
/// </summary>
public static class WindowsStorageLinks
{
    public static void ConnectShare(string uncPath, string? username, string? password)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Application-managed SMB connections currently require Windows.");
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var resource = new NetworkResource { Type = 1, RemoteName = $@"\\{parts[0]}\{parts[1]}" };
        var error = WNetAddConnection2(ref resource, password, username, 4 /* CONNECT_TEMPORARY */);
        if (error == 1219)
            throw new InvalidOperationException("Windows already has a connection to this server using different credentials in this logon session. Use matching credentials or a separate service logon. Existing connections were left intact.");
        if (error != 0) throw new Win32Exception(error, $"SMB connection failed (Windows error {error}): {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetworkResource resource, string? password, string? username, int flags);
}
