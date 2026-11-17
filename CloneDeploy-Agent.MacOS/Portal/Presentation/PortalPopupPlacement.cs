using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalPopupPlacement
{
    internal static void Configure(Window popup, Window owner, bool center = false)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        popup.WindowStartupLocation = WindowStartupLocation.Manual;
        popup.MaxHeight = Math.Min(popup.MaxHeight, Math.Max(100, area.Height / scale - 40));
        popup.MaxWidth = Math.Min(popup.MaxWidth, Math.Max(100, area.Width / scale - 40));
        void Place()
        {
            if (!popup.IsVisible) return;
            var width = popup.Bounds.Width * scale;
            var height = popup.Bounds.Height * scale;
            var inset = 20 * scale;
            popup.Position = new PixelPoint(
                (int)Math.Max(area.X, center ? area.X + (area.Width - width) / 2 : area.Right - width - inset),
                (int)Math.Max(area.Y, center ? area.Y + (area.Height - height) / 2 : area.Y + inset));
        }
        // Position after layout, using the actual popup size and the screen's usable
        // area (which excludes the menu bar and Dock). Reapply if wrapping changes size.
        popup.Opened += (_, _) => Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        popup.SizeChanged += (_, _) => Place();
    }
}
