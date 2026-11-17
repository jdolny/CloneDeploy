using System.Windows;
using System.Windows.Media;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

internal static class BrandAccent
{
    internal static void Apply(Window window, AgentBranding branding)
    {
        branding = branding.ForTheme(window.Resources["PortalDark"] is true);
        var brand = branding.IsConfigured ? branding : new AgentBranding();
        foreach (var (key, value) in new[] { ("BrandAccent", AgentBranding.ValidColor(brand.AccentColor) ? brand.AccentColor : "#2459D6"),
            ("BrandAccentForeground", brand.AccentForegroundColor), ("BrandAccentTint", brand.AccentTintColor), ("BrandAccentText", brand.AccentTextColor) })
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
            brush.Freeze(); window.Resources[key] = brush;
        }
        if (window.Resources["PortalDark"] is true)
        {
            var accent = ((SolidColorBrush)window.Resources["BrandAccent"]).Color;
            window.Resources["BrandAccentTint"] = new SolidColorBrush(Color.FromRgb(
                (byte)(32 + accent.R / 6), (byte)(32 + accent.G / 6), (byte)(32 + accent.B / 6)));
            window.Resources["BrandAccentText"] = new SolidColorBrush(Color.FromRgb(
                (byte)(155 + accent.R * 100 / 255), (byte)(155 + accent.G * 100 / 255), (byte)(155 + accent.B * 100 / 255)));
        }
    }
}
