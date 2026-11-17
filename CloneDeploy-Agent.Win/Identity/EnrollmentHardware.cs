using System.Management;

namespace CloneDeploy_Agent;

internal static class EnrollmentHardware
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public static string? ReadUuid()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            query.Options.Timeout = QueryTimeout;
            using var results = query.Get();

            foreach (ManagementObject row in results)
                using (row)
                    return row["UUID"]?.ToString();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Enrollment can still match an Ethernet MAC or a pre-provisioned hostname.
        }

        return null;
    }
}