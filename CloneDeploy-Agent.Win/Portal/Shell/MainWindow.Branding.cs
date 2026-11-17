using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private AgentBranding currentBranding = new();
    internal void ApplyBranding(AgentBranding branding)
    {
        currentBranding = branding;
        branding = branding.ForTheme(Resources["PortalDark"] is true);
        HelpPanel.SetHelpName(branding.HelpTabText);
        HelpPanel.SetSupportResponse(branding.SupportResponseText);
        PortalHeading.Text = branding.PortalTitleText;
        PortalHeading.ToolTip = PortalHeading.Text;
        OrganizationNameText.Text = branding.OrganizationName;
        OrganizationNameText.ToolTip = branding.OrganizationName;
        OrganizationNameText.Visibility = string.IsNullOrWhiteSpace(branding.OrganizationName) ? Visibility.Collapsed : Visibility.Visible;
        BuildNavigation();
        BrandAccent.Apply(this, branding);
        PortalHeader.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PortalSurface");
        PortalHeading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "PortalText");
        OrganizationNameText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "PortalText");
        OrganizationLogo.Source = null;
        OrganizationLogo.Visibility = Visibility.Collapsed;
        if (!branding.IsConfigured) return;
        if (AgentBranding.ValidColor(branding.BackgroundColor))
        {
            PortalHeader.Background = (Brush)new BrushConverter().ConvertFromString(branding.BackgroundColor)!;
            PortalHeading.Foreground = (Brush)new BrushConverter().ConvertFromString(branding.ForegroundColor)!;
            OrganizationNameText.Foreground = PortalHeading.Foreground;
        }
        if (branding.LogoPng is not { Length: > 0 and <= CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes }) return;
        try
        {
            using var stream = new MemoryStream(branding.LogoPng);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            OrganizationLogo.Source = bitmap; OrganizationLogo.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        { /* A missing or invalid logo leaves the portal heading visible. */ }
    }
}
