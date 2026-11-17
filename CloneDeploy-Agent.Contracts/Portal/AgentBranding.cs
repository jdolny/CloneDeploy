namespace CloneDeploy_Agent.Contracts;

public enum NotificationBranding { Organization = 0, CloneDeploy = 1 }
public enum NotificationKind { Information = 0, Success = 1, Warning = 2, Critical = 3 }

public sealed record AgentBranding
{
    public const int MaximumLogoBytes = 64 * 1024;

    public string Version { get; init; } = "default";
    public string OrganizationName { get; init; } = "";
    public string PortalTitle { get; init; } = "Self Service Portal";
    public const string DefaultSupportResponseMessage = "Our support team will respond when available.";
    public string SupportResponseMessage { get; init; } = DefaultSupportResponseMessage;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SupportResponseText => Label(SupportResponseMessage, DefaultSupportResponseMessage);
    public string HelpTabName { get; init; } = "Help";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PortalTitleText => Label(PortalTitle, "Self Service Portal");
    [System.Text.Json.Serialization.JsonIgnore]
    public string HelpTabText => Label(HelpTabName, "Help");
    private static string Label(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    public string Tagline { get; init; } = "";
    public string BackgroundColor { get; init; } = "#2459D6";
    public string AccentColor { get; init; } = "#2459D6";
    public string? DarkBackgroundColor { get; init; }
    public string? DarkAccentColor { get; init; }
    public AgentBranding ForTheme(bool dark) => dark ? this with
    {
        BackgroundColor = ValidColor(DarkBackgroundColor) ? DarkBackgroundColor! : BackgroundColor,
        AccentColor = ValidColor(DarkAccentColor) ? DarkAccentColor! : AccentColor
    } : this;
    public string AccentForegroundColor => (this with { BackgroundColor = AccentColor }).ForegroundColor;
    public string AccentTintColor => MixAccent(.9);
    public string AccentTextColor
    {
        get
        {
            var color = ValidColor(AccentColor) ? AccentColor : "#2459D6";
            for (var i = 0; i < 12 && (this with { BackgroundColor = color }).ForegroundColor != "#FFFFFF"; i++)
                color = "#" + string.Concat(new[] { 1, 3, 5 }.Select(start => ((int)(Convert.ToInt32(color.Substring(start, 2), 16) * .8)).ToString("X2")));
            return color;
        }
    }
    private string MixAccent(double white)
    {
        var color = ValidColor(AccentColor) ? AccentColor : "#2459D6";
        return "#" + string.Concat(new[] { 1, 3, 5 }.Select(start => ((int)Math.Round(Convert.ToInt32(color.Substring(start, 2), 16) * (1 - white) + 255 * white)).ToString("X2")));
    }
    public byte[]? LogoPng { get; init; }
    public bool HasIdentity => !string.IsNullOrWhiteSpace(OrganizationName) || !string.IsNullOrWhiteSpace(Tagline) || LogoPng is { Length: > 0 };
    public bool IsConfigured => HasIdentity ||
        ValidColor(DarkBackgroundColor) || ValidColor(DarkAccentColor) ||
        !string.Equals(BackgroundColor, "#2459D6", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(AccentColor, "#2459D6", StringComparison.OrdinalIgnoreCase);
    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#' && color.AsSpan(1).ToArray().All(Uri.IsHexDigit);
    public string ForegroundColor
    {
        get
        {
            if (!ValidColor(BackgroundColor)) return "#FFFFFF";
            double Channel(int start) { var c = Convert.ToInt32(BackgroundColor.Substring(start, 2), 16) / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            var luminance = .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
            return luminance > .179 ? "#111827" : "#FFFFFF";
        }
    }
}
public sealed record AgentNotificationSnapshot(AgentPortalNotification[] Notifications, AgentBranding Branding)
{
    public AgentFeatures Features { get; init; } = new();
}
