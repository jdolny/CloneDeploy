using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS.Portal;

// All popup types share the portal's organization identity, regardless of notice kind.
internal static class PortalPopupBranding
{
    private static AgentBranding current = new();
    private static event Action? Changed;
    internal static void Update(AgentBranding value) { current = value; Changed?.Invoke(); }
    internal static string WindowTitle(AgentBranding value) =>
        $"{(string.IsNullOrWhiteSpace(value.OrganizationName) ? "CloneDeploy" : value.OrganizationName.Trim())} - {value.PortalTitleText}";

    internal static Control Header(Window window, string heading)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*") };
        var logo = new ContentControl { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(logo); Grid.SetColumn(title, 1); row.Children.Add(title);
        Bitmap? bitmap = null;
        void Apply()
        {
            window.Title = WindowTitle(current);
            logo.Content = null; bitmap?.Dispose(); bitmap = null;
            if (current.LogoPng is { Length: > 0 and <= CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes } bytes)
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    bitmap = Bitmap.DecodeToWidth(stream, 240);
                    logo.Content = new Image { Source = bitmap, MaxWidth = 80, Height = 32, Stretch = Stretch.Uniform };
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
            }
            logo.Content ??= FallbackLogo();
        }
        Apply(); Changed += Apply;
        window.Closed += (_, _) => { Changed -= Apply; logo.Content = null; bitmap?.Dispose(); bitmap = null; };
        return row;
    }
    private static Control FallbackLogo()
    {
        var canvas = new Canvas { Width = 268.435, Height = 185.087 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M91.37,44.609h-34.435c-2.053,0-3.717,1.664-3.717,3.717v86.478c0,2.053,1.664,3.717,3.717,3.717h70.826c2.053,0,3.717,1.664,3.717,3.717v39.13c0,2.053-1.664,3.717-3.717,3.717H24.261c-13.399,0-24.261-10.862-24.261-24.261V24.261C0,10.862,10.862,0,24.261,0h103.5C129.814,0,131.478,1.664,131.478,3.717v57.913c0,2.053-1.664,3.717-3.717,3.717h-28.957c-2.053,0-3.717-1.664-3.717-3.717v-13.304c0-2.053-1.664-3.717-3.717-3.717Z"), Fill = Brush.Parse("#182337") });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M177.065,140.478h34.435c2.053,0,3.717-1.664,3.717-3.717V50.283c0-2.053-1.664-3.717-3.717-3.717h-70.826c-2.053,0-3.717-1.664-3.717-3.717V3.717C136.957,1.664,138.621,0,140.674,0h103.5c13.399,0,24.261,10.862,24.261,24.261v136.565c0,13.399-10.862,24.261-24.261,24.261h-103.5c-2.053,0-3.717-1.664-3.717-3.717v-57.913c0-2.053,1.664-3.717,3.717-3.717h28.957c2.053,0,3.717,1.664,3.717,3.717v13.304c0,2.053,1.664,3.717,3.717,3.717Z"), Fill = Brush.Parse("#2490FF") });
        return new Viewbox { Child = canvas, Width = 26, Height = 22, Stretch = Stretch.Uniform };
    }

}
