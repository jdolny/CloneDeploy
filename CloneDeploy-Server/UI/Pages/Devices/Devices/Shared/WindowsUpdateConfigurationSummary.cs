using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.UI.Pages.Devices.Devices.Shared;

/// <summary>Explains reported update behavior without predicting execution or resolving GPO/MDM precedence.</summary>
public sealed record WindowsUpdateConfigurationSummary(string Download, string Installation, string Restart)
{
    public static WindowsUpdateConfigurationSummary From(InventoryWindowsUpdateConfiguration configuration)
    {
        var settings = configuration.Settings;
        var policy = settings.Where(x => x.Source is "Policy registry (origin unknown)" or "Group Policy (RSoP)").ToList();
        var mdm = settings.Where(x => x.Source == "MDM policy registry").ToList();
        var local = settings.Where(x => x.Source == "Local Windows Update settings").ToList();
        var downloads = new List<string>();
        var installations = new List<string>();
        var restarts = new List<string>();
        Describe(policy, "Policy");
        Describe(mdm, "MDM");
        if (restarts.Count == 0 && Hours(local) is { } localHours)
            restarts.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.LocalSettingsReport0EffectivePolicyIsUnknown, localHours));
        return new(
            downloads.Count == 0 ? UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.UnknownAutomaticDownloadBehaviorWasNotReported) : string.Join("; ", downloads),
            installations.Count == 0 ? UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.NoExplicitScheduleDetectedWindowsMayChooseInstallationTiming) : string.Join("; ", installations),
            restarts.Count == 0 ? UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.UnknownNoActiveHoursProtectionWasConfirmed) : string.Join("; ", restarts));

        void Describe(List<InventoryWindowsUpdateSetting> source, string label)
        {
            var mode = Value(source, "Automatic update behavior");
            var disabled = Enabled(source, "Disable automatic updates") || mode == "Automatic updates disabled";
            if (disabled)
            {
                downloads.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text0AutomaticUpdatesDisabled, UiText.Label(label)));
                installations.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text0AutomaticUpdatesDisabled, UiText.Label(label)));
            }
            else if (mode is not null)
            {
                downloads.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text01, UiText.Label(label), (mode switch
                {
                    "Notify before download" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.NotifyBeforeDownloading),
                    "Download automatically; notify before installation" or "Download automatically; scheduled installation" or
                    "Notify for installation and restart (server)" or "Install automatically; notify before restart" or
                    "Install and restart during maintenance" or "Install and restart at scheduled time" or
                    "Install and restart automatically" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.AutomaticDownloadExactTimeIsNotReported),
                    _ => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.DownloadBehaviorUnknown)
                })));
                var scheduled = mode is "Download automatically; scheduled installation" or "Install and restart at scheduled time";
                var installation = mode switch
                {
                    "Notify before download" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.UserActionRequiredBeforeDownloadInstallationTimingIsNot),
                    "Download automatically; notify before installation" or "Notify for installation and restart (server)" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.NotifyBeforeInstallation),
                    "Install and restart during maintenance" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.AutomaticMaintenanceNoFixedInstallationTimeReported),
                    "Install automatically; notify before restart" or "Install and restart automatically" => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.AutomaticInstallationExactTimeIsNotReported),
                    _ => UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.InstallationBehaviorUnknown)
                };
                if (scheduled)
                {
                    var day = Value(source, "Configured installation day");
                    var time = Value(source, "Configured installation time");
                    installation = day is not null && time is not null && !day.StartsWith("Unknown") && !time.StartsWith("Unknown")
                        ? UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.ConfiguredFor01WeeklyRestrictionsMayApply, day, time)
                        : UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.ScheduledInstallationEnabledButDayTimeIsIncomplete);
                    if (Enabled(source, "Use automatic maintenance for installation"))
                        installation = UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.AutomaticMaintenanceIsEnabledInsteadOfTheFixedInstallation);
                }
                installations.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text01, UiText.Label(label), installation));
            }
            else if (Value(source, "Configured installation day") is not null || Value(source, "Configured installation time") is not null)
                installations.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text0ScheduleValuesExistButScheduledInstallationIsNot, UiText.Label(label)));

            // Classic policy requires SetActiveHours. MDM configures the range directly.
            if ((label == "Policy" && Enabled(source, "Active hours policy enabled") || label == "MDM") && Hours(source) is { } hours)
                restarts.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text0Specifies1DeadlinesCanOverride, UiText.Label(label), hours));
            else if (Enabled(source, "Active hours policy enabled"))
                restarts.Add(UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text0ActiveHoursProtectionEnabledButTheTimeRange, UiText.Label(label)));
        }
    }

    private static string? Value(List<InventoryWindowsUpdateSetting> settings, string name)
    {
        var values = settings.Where(x => x.Name == name).Select(x => x.Value).Distinct().ToList();
        return values.Count switch { 0 => null, 1 => values[0], _ => "Unknown — conflicting values" };
    }

    private static bool Enabled(List<InventoryWindowsUpdateSetting> settings, string name) => Value(settings, name) == "Enabled (1)";

    private static string? Hours(List<InventoryWindowsUpdateSetting> settings)
    {
        var start = Value(settings, "Active hours start");
        var end = Value(settings, "Active hours end");
        const string suffix = " (device local time)";
        return start?.EndsWith(suffix) == true && end?.EndsWith(suffix) == true
            ? UiText.Get(LocKeys.Ui.WindowsUpdateConfigurationSummary.Text01DeviceLocalTime, start[..^suffix.Length], end[..^suffix.Length]) : null;
    }
}
