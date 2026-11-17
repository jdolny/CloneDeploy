using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace CloneDeploy_Agent.UI;

internal static class HelpScreenshot
{
    private const int CaptureDelayMilliseconds = 300;
    private const uint PreserveSizeZOrderAndActivation = 0x0015;

    internal static async Task<byte[]?> CaptureAsync(Window owner)
    {
        if (!Ready(owner)) return null;
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle).Bounds;
        owner.Hide();
        byte[] data;
        try
        {
            await Task.Delay(CaptureDelayMilliseconds);
            using var bitmap = new Drawing.Bitmap(screen.Width, screen.Height);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(screen.Location, Drawing.Point.Empty, screen.Size);
            data = Encode(bitmap);
        }
        finally { owner.Show(); owner.Activate(); }
        return Preview(owner, data, true) ? data : null;
    }
    internal static byte[] Encode(Drawing.Bitmap bitmap)
    {
        var scale = Math.Min(1d, (double)CloneDeploy_Agent.Contracts.HelpAttachmentLimits.MaximumDimension / Math.Max(bitmap.Width, bitmap.Height));
        while (true)
        {
            var width = Math.Max(1, (int)(bitmap.Width * scale));
            var height = Math.Max(1, (int)(bitmap.Height * scale));
            using var stream = new MemoryStream();
            if (width == bitmap.Width && height == bitmap.Height) bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png);
            else
            {
                using var scaled = new Drawing.Bitmap(width, height);
                using (var graphics = Drawing.Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.DrawImage(bitmap, 0, 0, width, height);
                }
                scaled.Save(stream, Drawing.Imaging.ImageFormat.Png);
            }
            if (stream.Length <= CloneDeploy_Agent.Contracts.HelpAttachmentLimits.ScreenshotBytes) return stream.ToArray();
            if (width <= 400 || height <= 300) throw new IOException(PortalText.Get("Screenshot.ImageTooLargeHint"));
            scale *= 0.85;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static bool Ready(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Owner = owner, Title = PortalText.Get("HelpDesk.GetYourScreenshotReady"), Width = 460,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        PortalTheme.Attach(dialog, () => { });
        dialog.Loaded += (_, _) =>
        {
            var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle).WorkingArea;
            var handle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
            if (!GetWindowRect(handle, out var bounds)) return;
            var gap = (int)Math.Round(16 * VisualTreeHelper.GetDpi(dialog).DpiScaleX);
            // Use physical screen coordinates to handle monitors with different DPI settings.
            SetWindowPos(handle, IntPtr.Zero, Math.Max(area.Left, area.Right - (bounds.Right - bounds.Left) - gap),
                Math.Max(area.Top, area.Bottom - (bounds.Bottom - bounds.Top) - gap), 0, 0, PreserveSizeZOrderAndActivation);
        };
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Screenshot.PrepareWindowsInstructions"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Screenshot.MultipleMonitorsHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Screenshot.ReviewBeforeSendingHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = PortalText.Get("Common.Cancel"), IsCancel = true, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) };
        var capture = new Button { Content = PortalText.Get("HelpDesk.TakeScreenshot"), Padding = new Thickness(12, 8, 12, 8) };
        capture.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(capture); panel.Children.Add(buttons);
        return dialog.ShowDialog() == true;
    }
    internal static bool Preview(Window owner, byte[] data, bool confirm)
    {
        using var stream = new MemoryStream(data);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0]; image.Freeze();
        var panel = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var dialog = new Window { Owner = owner, Title = confirm ? PortalText.Get("Screenshot.ReviewBeforeAttaching") : PortalText.Get("HelpDesk.TicketScreenshot"),
            Width = 900, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        PortalTheme.Attach(dialog, () => { });
        var close = new Button { Content = confirm ? PortalText.Get("Common.Cancel") : PortalText.Get("HelpDesk.Close"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8) };
        close.Click += (_, _) => dialog.DialogResult = false; buttons.Children.Add(close);
        if (confirm)
        {
            var attach = new Button { Content = PortalText.Get("HelpDesk.AttachScreenshot"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8) };
            attach.Click += (_, _) => dialog.DialogResult = true; buttons.Children.Add(attach);
        }
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var notice = new TextBlock { Text = confirm ? PortalText.Get("Screenshot.PrivacyReminder") : "",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(notice, Dock.Top); panel.Children.Add(notice);
        var picture = new Image { Source = image, Stretch = Stretch.Uniform };
        var viewer = new ScrollViewer { Content = picture, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var zoom = new Button { Content = PortalText.Get("HelpDesk.ActualSize"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8) };
        var actualSize = false;
        zoom.Click += (_, _) =>
        {
            actualSize = !actualSize;
            picture.Width = actualSize ? image.PixelWidth : double.NaN;
            picture.Height = actualSize ? image.PixelHeight : double.NaN;
            viewer.HorizontalScrollBarVisibility = viewer.VerticalScrollBarVisibility = actualSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            zoom.Content = actualSize ? PortalText.Get("HelpDesk.FitToWindow") : PortalText.Get("HelpDesk.ActualSize");
        };
        buttons.Children.Insert(0, zoom);
        panel.Children.Add(viewer);
        return dialog.ShowDialog() == true;
    }
}

