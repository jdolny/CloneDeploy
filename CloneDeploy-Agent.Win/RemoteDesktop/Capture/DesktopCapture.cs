// Adapted from ControlR ScreenGrabberWindows.GetBitBltCapture (MIT).
// Copyright (c) 2026 Jared Goodwin. See ControlR-LICENSE.txt.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Captures the primary display using ControlR's GDI fallback approach, encoded as bounded JPEG frames.</summary>
internal static class DesktopCapture
{
    private const long MaximumDisplayPixels = 40000000;
    private const uint SourceCopyRasterOperation = 0x00CC0020;

    internal static byte[] Capture()
        => CaptureMonitor(null);

    internal static byte[] CaptureMonitor(string? monitorId, DesktopPipe.CaptureQuality? quality = null, DesktopPipe.FrameChanges? changes = null)
    {
        using var desktop = new DesktopAttachment();
        var monitor = DesktopMonitors.Resolve(monitorId);
        var width = monitor.Width;
        var height = monitor.Height;
        if (width <= 0 || height <= 0) throw new DesktopUnavailableException("The display is temporarily unavailable.");
        if ((long)width * height > MaximumDisplayPixels) throw new IOException("Unsupported display size.");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var source = DesktopNative.GetDC(0);
            if (source == 0) throw new DesktopUnavailableException("The desktop display context is temporarily unavailable.");
            nint target = 0;
            try
            {
                target = graphics.GetHdc();
                if (!DesktopNative.BitBlt(target, 0, 0, width, height, source, monitor.Left, monitor.Top, SourceCopyRasterOperation)) // SRCCOPY
                    throw new DesktopUnavailableException("Windows could not capture the current desktop.");
                var cursor = new DesktopNative.CursorInfo { Size = Marshal.SizeOf<DesktopNative.CursorInfo>() };
                if (DesktopNative.GetCursorInfo(ref cursor) && cursor.Flags == 1)
                    DesktopNative.DrawIconEx(target, cursor.X - monitor.Left, cursor.Y - monitor.Top, cursor.Cursor, 0, 0, 0, 0, 3);
            }
            finally
            {
                if (target != 0) graphics.ReleaseHdc(target);
                DesktopNative.ReleaseDC(0, source);
            }
        }
        quality ??= DesktopPipe.Quality("balanced");
        var scale = Math.Min(1d, (double)quality.MaxWidth / width);
        using var resized = new Bitmap(bitmap, new Size((int)(width * scale), Math.Max(1, (int)(height * scale))));
        if (changes is not null)
        {
            var pixels = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (!changes.ShouldSend(pixels.Scan0, pixels.Stride, resized.Width * 4, resized.Height,
                    $"{monitor.Id}:{monitor.Left}:{monitor.Top}:{width}:{height}:{resized.Width}:{resized.Height}:{quality.JpegQuality}")) return [];
            }
            finally { resized.UnlockBits(pixels); }
        }
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality.JpegQuality);
        resized.Save(output, ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid), parameters);
        if (output.Length > DesktopPipe.MaxFrameBytes) throw new IOException("Desktop frame is too large.");
        return output.ToArray();
    }
}
