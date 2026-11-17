using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalScreenshot
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestScreenCaptureAccess();

    public static async Task<byte[]?> CaptureAsync(Window owner, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new IOException(PortalText.Get("Screenshot.RequiresMacOS"));
        var ready = new Window { Title = PortalText.Get("HelpDesk.GetYourScreenshotReady"), Width = 390, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(PortalPopupBranding.Header(ready, PortalText.Get("HelpDesk.GetYourScreenshotReady")));
        panel.Children.Add(new TextBlock { Text = PortalText.Get("Screenshot.PrepareMacInstructions"),
            TextWrapping = TextWrapping.Wrap });
        var capture = new Button { Content = PortalText.Get("HelpDesk.ChooseScreenshot") };
        var cancel = new Button { Content = PortalText.Get("Common.Cancel") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(capture); buttons.Children.Add(cancel); panel.Children.Add(buttons); ready.Content = panel;
        capture.Click += (_, _) => ready.Close(true); cancel.Click += (_, _) => ready.Close(false);
        PortalPopupPlacement.Configure(ready, owner);
        using var close = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ready.Close(false)));
        if (!await ready.ShowDialog<bool>(owner)) return null;
        ct.ThrowIfCancellationRequested();
        if (!CGPreflightScreenCaptureAccess() && !CGRequestScreenCaptureAccess())
            throw new IOException(PortalText.Get("Screenshot.MacPermissionInstructions"));
        var directory = Path.Combine(Path.GetTempPath(), "clonedeploy-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(directory, "capture.png");
        try
        {
            owner.Hide();
            await Task.Delay(250, ct);
            var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
            foreach (var arg in new[] { "-i", "-x", "-t", "png", file }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException(PortalText.Get("Screenshot.StartFailed"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(); }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(file)) return null; // Escape is cancellation, not a failed ticket.
            if (process.ExitCode != 0) throw new IOException(PortalText.Get("Screenshot.CaptureFailedPermissionHint"));
            using var bitmap = new Bitmap(file);
            var size = bitmap.PixelSize;
            var factor = Math.Min(1d, (double)CloneDeploy_Agent.Contracts.HelpAttachmentLimits.MaximumDimension / Math.Max(size.Width, size.Height));
            while (true)
            {
                using var scaled = bitmap.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(size.Width * factor)), Math.Max(1, (int)(size.Height * factor))));
                using var output = new MemoryStream(); scaled.Save(output, PngBitmapEncoderOptions.Default);
                if (output.Length <= CloneDeploy_Agent.Contracts.HelpAttachmentLimits.ScreenshotBytes) return output.ToArray();
                factor *= .8;
                if (factor < .1) throw new IOException(PortalText.Get("Screenshot.SelectSmallerArea"));
            }
        }
        finally
        {
            try { File.Delete(file); Directory.Delete(directory); } catch (IOException) { }
            if (!ct.IsCancellationRequested) { owner.Show(); owner.Activate(); }
        }
    }
}
