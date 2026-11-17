using System.Globalization;
using System.Management;
using System.Security;
using CloneDeploy_Agent.Contracts;
using Microsoft.Win32;

namespace CloneDeploy_Agent;

/// <summary>Reads update policy and local settings without scanning, installing, or changing Windows configuration.</summary>
public static class WindowsUpdateConfigurationCollector
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string MdmPath = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";
    private const string LocalPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    // Keep this allowlist explicit: never inventory arbitrary policy values or management credentials.
    private static readonly (string Key, string Label)[] UpdateSettings =
    [
        ("WUServer", "WSUS server"), ("WUStatusServer", "WSUS reporting server"),
        ("TargetGroup", "WSUS target group"), ("TargetGroupEnabled", "WSUS targeting enabled"),
        ("SetPolicyDrivenUpdateSourceForFeatureUpdates", "Feature update source"),
        ("SetPolicyDrivenUpdateSourceForQualityUpdates", "Quality update source"),
        ("SetPolicyDrivenUpdateSourceForDriverUpdates", "Driver update source"),
        ("SetPolicyDrivenUpdateSourceForOtherUpdates", "Other update source"),
        ("DoNotConnectToWindowsUpdateInternetLocations", "Block public Windows Update connections"),
        ("DisableWindowsUpdateAccess", "Disable Windows Update access"),
        ("SetDisableUXWUAccess", "Remove Windows Update user controls"),
        ("ExcludeWUDriversInQualityUpdate", "Exclude drivers from quality updates"),
        ("ProductVersion", "Target Windows product"), ("TargetReleaseVersion", "Target version policy enabled"),
        ("TargetReleaseVersionInfo", "Target Windows version"),
        ("DeferFeatureUpdates", "Feature update deferral enabled"),
        ("DeferFeatureUpdatesPeriodInDays", "Feature update deferral (days)"),
        ("DeferQualityUpdates", "Quality update deferral enabled"),
        ("DeferQualityUpdatesPeriodInDays", "Quality update deferral (days)"),
        ("PauseFeatureUpdates", "Pause feature updates"), ("PauseQualityUpdates", "Pause quality updates"),
        ("PauseFeatureUpdatesStartTime", "Feature update pause start"),
        ("PauseQualityUpdatesStartTime", "Quality update pause start"),
        ("SetActiveHours", "Active hours policy enabled"),
        ("ActiveHoursStart", "Active hours start"), ("ActiveHoursEnd", "Active hours end"),
        ("SetComplianceDeadline", "Update deadlines enabled"),
        ("ConfigureDeadlineForFeatureUpdates", "Feature update deadline (days)"),
        ("ConfigureDeadlineForQualityUpdates", "Quality update deadline (days)"),
        ("ConfigureDeadlineGracePeriod", "Restart grace period (days)"),
        ("ConfigureDeadlineGracePeriodForFeatureUpdates", "Feature update restart grace (days)"),
        ("ConfigureDeadlineNoAutoReboot", "Prevent automatic restart before deadline")
    ];

    private static readonly (string Key, string Label)[] AutomaticSettings =
    [
        ("NoAutoUpdate", "Disable automatic updates"), ("AUOptions", "Automatic update behavior"),
        ("UseWUServer", "Use WSUS"), ("UseUpdateClassPolicySource", "Use per-class update source policy"),
        ("ScheduledInstallDay", "Configured installation day"),
        ("ScheduledInstallTime", "Configured installation time"),
        ("ScheduledInstallEveryWeek", "Install every week"),
        ("ScheduledInstallFirstWeek", "Install during first week"),
        ("ScheduledInstallSecondWeek", "Install during second week"),
        ("ScheduledInstallThirdWeek", "Install during third week"),
        ("ScheduledInstallFourthWeek", "Install during fourth week"),
        ("AutomaticMaintenanceEnabled", "Use automatic maintenance for installation"),
        ("NoAutoRebootWithLoggedOnUsers", "Prevent scheduled restart with signed-in users"),
        ("AlwaysAutoRebootAtScheduledTime", "Force scheduled automatic restart"),
        ("AlwaysAutoRebootAtScheduledTimeMinutes", "Scheduled restart delay (minutes)")
    ];

    public static InventoryWindowsUpdateConfiguration Collect(ILogger logger)
    {
        var snapshot = new InventoryWindowsUpdateConfiguration
        {
            CollectedAtUtc = DateTime.UtcNow,
            TimeZone = TimeZoneInfo.Local.DisplayName
        };
        var policies = ReadGroupPolicies(snapshot, logger);
        Read(PolicyPath, "Policy registry (origin unknown)", UpdateSettings);
        Read($"{PolicyPath}\\AU", "Policy registry (origin unknown)", AutomaticSettings);
        Read(
            MdmPath,
            "MDM policy registry",
            UpdateSettings.Where(x => x.Key is not ("TargetReleaseVersion" or "TargetReleaseVersionInfo"))
                .Concat(AutomaticSettings.Where(x => x.Key != "AUOptions")).Concat(
                [
                    ("AllowAutoUpdate", "Automatic update behavior"),
                    ("TargetReleaseVersion", "Target Windows version"),
                    ("BranchReadinessLevel", "Feature update readiness level")
                ]));
        Read(
            @"SOFTWARE\Microsoft\PolicyManager\current\device\ControlPolicyConflict",
            "MDM policy registry",
            [("MDMWinsOverGP", "Prefer MDM over equivalent Group Policy settings")]);
        Read(
            LocalPath,
            "Local Windows Update settings",
            [
                ("ActiveHoursStart", "Active hours start"), ("ActiveHoursEnd", "Active hours end"),
                ("PauseUpdatesExpiryTime", "Update pause expiry"),
                ("PauseFeatureUpdatesEndTime", "Feature update pause expiry"),
                ("PauseQualityUpdatesEndTime", "Quality update pause expiry")
            ]);
        snapshot.Activity = WindowsUpdateActivityCollector.Collect(logger);

        return snapshot;

        void Read(string path, string source, IEnumerable<(string Key, string Label)> settings)
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = machine.OpenSubKey(path);

                if (key is null)
                    return;

                foreach (var setting in settings)
                {
                    // PolicyManager may contain defaults with ProviderSet=0; do not label these as configured MDM policy.
                    if (source == "MDM policy registry"
                        && key.GetValue($"{setting.Key}_ProviderSet") is int providerSet
                        && providerSet == 0)
                        continue;

                    var value = key.GetValue(setting.Key, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

                    if (value is not (int or long or string))
                        continue;

                    policies.TryGetValue($"{path}\\{setting.Key}", out var gpo);
                    snapshot.Settings.Add(new(
                        setting.Label,
                        FormatValue(setting.Key, value),
                        gpo is null ? source : "Group Policy (RSoP)",
                        gpo));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                snapshot.UnavailableSources.Add($"{source}: {path}");
                logger.LogDebug(ex, $"Windows Update configuration source unavailable: {path}");
            }
        }
    }

    /// <summary>Formats documented policy values without treating missing or unrecognized values as disabled.</summary>
    public static string FormatValue(string name, object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Unknown";

        if (!int.TryParse(text, CultureInfo.InvariantCulture, out var number))
            return text;

        if (name == "AUOptions")
            return number switch
            {
                2 => "Notify before download",
                3 => "Download automatically; notify before installation",
                4 => "Download automatically; scheduled installation",
                5 => "Local administrator chooses",
                7 => "Notify for installation and restart (server)",
                _ => $"Unknown ({number})"
            };
        // CSP AllowAutoUpdate uses different values from Group Policy AUOptions.
        if (name == "AllowAutoUpdate")
            return number switch
            {
                0 => "Notify before download",
                1 => "Install automatically; notify before restart",
                2 => "Install and restart during maintenance",
                3 => "Install and restart at scheduled time",
                4 => "Install and restart automatically",
                5 => "Automatic updates disabled",
                _ => $"Unknown ({number})"
            };

        if (name == "ScheduledInstallDay")
            return number switch
            {
                0 => "Every day",
                >= 1 and <= 7 => ((DayOfWeek)(number - 1)).ToString(),
                _ => $"Unknown ({number})"
            };

        if (name is "ScheduledInstallTime" or "ActiveHoursStart" or "ActiveHoursEnd")
            return number is >= 0 and <= 23 ? $"{number:00}:00 (device local time)" : $"Unknown ({number})";

        if (name.StartsWith("SetPolicyDrivenUpdateSourceFor", StringComparison.Ordinal))
            return number switch
            {
                0 => "Windows Update",
                1 => "WSUS",
                _ => $"Unknown ({number})"
            };

        if (name.EndsWith("Days", StringComparison.Ordinal)
            || name.EndsWith("Minutes", StringComparison.Ordinal)
            ||
            name.StartsWith("ConfigureDeadline", StringComparison.Ordinal)
            && name != "ConfigureDeadlineNoAutoReboot"
            ||
            name == "BranchReadinessLevel")
            return text;

        return number switch
        {
            0 => "Disabled (0)",
            1 => "Enabled (1)",
            _ => text
        };
    }

    private static Dictionary<string, string> ReadGroupPolicies(
        InventoryWindowsUpdateConfiguration snapshot,
        ILogger logger)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Query(
                "SELECT id, name FROM RSOP_GPO",
                row =>
                {
                    if (row["id"] is string id && row["name"] is string name)
                        names[id] = name;
                });
            Query(
                "SELECT registryKey, valueName, GPOID FROM RSOP_RegistryPolicySetting WHERE precedence=1 AND deleted=FALSE",
                row =>
                {
                    var path = Convert.ToString(row["registryKey"]) ?? "";

                    foreach (var prefix in new[] { @"HKEY_LOCAL_MACHINE\", @"HKLM\", @"MACHINE\" })
                        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            path = path[prefix.Length..];

                    if (!path.Equals(PolicyPath, StringComparison.OrdinalIgnoreCase)
                        && !path.Equals($"{PolicyPath}\\AU", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (row["GPOID"] is string id && !string.IsNullOrWhiteSpace(id))
                        result[$"{path}\\{row["valueName"]}"] = names.GetValueOrDefault(id, id);
                });
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            snapshot.UnavailableSources.Add("Group Policy attribution (RSoP)");
            logger.LogDebug(ex, "Windows Update Group Policy attribution unavailable");
        }

        return result;

        static void Query(string query, Action<ManagementBaseObject> read)
        {
            using var searcher = new ManagementObjectSearcher(@"root\RSOP\Computer", query);
            searcher.Options.Timeout = QueryTimeout;
            using var rows = searcher.Get();

            foreach (ManagementBaseObject row in rows)
                using (row)
                    read(row);
        }
    }
}