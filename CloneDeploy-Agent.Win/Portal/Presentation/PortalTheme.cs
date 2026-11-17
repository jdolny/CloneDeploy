using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CloneDeploy_Agent.UI;

internal static class PortalTheme
{
    internal static void Attach(Window window, Action refreshBranding)
    {
        void Refresh()
        {
            var dark = false;
            try { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
            Apply(window, dark);
            refreshBranding();
        }
        UserPreferenceChangedEventHandler changed = (_, _) =>
        {
            if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.InvokeAsync(Refresh);
        };
        SystemEvents.UserPreferenceChanged += changed;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= changed;
        Refresh();
    }

    internal static void Apply(Window window, bool dark)
    {
        window.Resources["PortalDark"] = dark;
        foreach (var (key, light, night) in new[] {
            ("PortalSurface", "#FFFFFF", "#202020"), ("PortalRaised", "#F5F7FA", "#2D2D2D"),
            ("PortalText", "#172B3A", "#F2F2F2"), ("PortalMuted", "#627383", "#BFC5CD"),
            ("PortalBorder", "#E4E9EE", "#454545"), ("PortalHover", "#F0F4F7", "#383838") })
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? night : light)!;
            brush.Freeze(); window.Resources[key] = brush;
        }
    }
}
