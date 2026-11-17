// Adapted from ControlR ScreenGrabberMac, CoreGraphicsHelper and MacInputSimulation (MIT).
// Copyright (c) 2026 Jared Goodwin. See ControlR-LICENSE.txt.
using System.Runtime.InteropServices;
using CloneDeploy_Agent.RemoteDesktop;
using SkiaSharp;

namespace CloneDeploy_Agent.MacOS.Portal;

internal static class MacDesktopNative
{
    private const uint Utf8Encoding = 0x08000100;
    private const int MaximumDisplayPixels = 80000000;
    private const uint LittleEndianPremultipliedFirst = 0x2002;

    internal const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    internal const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] internal struct Point(double x, double y) { public double X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public double X, Y, Width, Height; }
    internal sealed record Monitor(string Id, int Left, int Top, int Width, int Height, bool Primary);

    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGPreflightPostEventAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CGRequestPostEventAccess();
    [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint max, [Out] uint[] displays, out uint count);
    [DllImport(CG)] internal static extern Rect CGDisplayBounds(uint display);
    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern nint CGDisplayCreateImage(uint display);
    [DllImport(CG)] private static extern nuint CGImageGetWidth(nint image);
    [DllImport(CG)] private static extern nuint CGImageGetHeight(nint image);
    [DllImport(CG)] private static extern nint CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] private static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bits, nuint stride, nint colors, uint flags);
    [DllImport(CG)] private static extern void CGContextDrawImage(nint context, Rect rect, nint image);
    [DllImport(CG)] internal static extern nint CGEventCreate(nint source);
    [DllImport(CG)] internal static extern Point CGEventGetLocation(nint value);
    [DllImport(CG)] internal static extern nint CGSessionCopyCurrentDictionary();
    [DllImport(CF)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CF)] private static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(nint value);
    [DllImport(CF)] internal static extern void CFRelease(nint value);
    [DllImport("libc")] internal static extern uint geteuid();

    internal static bool ConsoleAvailable()
    {
        if (!OperatingSystem.IsMacOS() || MacConsoleSession.Current()?.Uid != geteuid()) return false;
        var dict = CGSessionCopyCurrentDictionary();
        if (dict == 0) return false;
        var key = CFStringCreateWithCString(0, "CGSSessionScreenIsLocked", Utf8Encoding);
        try { var locked = CFDictionaryGetValue(dict, key); return locked == 0 || !CFBooleanGetValue(locked); }
        finally { if (key != 0) CFRelease(key); CFRelease(dict); }
    }

    internal static Monitor[] Monitors()
    {
        var ids = new uint[32];
        if (CGGetActiveDisplayList(32, ids, out var count) != 0 || count > 32) throw new IOException(PortalText.Get("RemoteSupport.DisplaysUnavailable"));
        return ids.Take((int)count).Select(id =>
        {
            var rect = CGDisplayBounds(id);
            return new Monitor(id.ToString(System.Globalization.CultureInfo.InvariantCulture), (int)rect.X, (int)rect.Y,
                (int)rect.Width, (int)rect.Height, id == CGMainDisplayID());
        }).Where(x => x.Width > 0 && x.Height > 0).OrderByDescending(x => x.Primary).ToArray();
    }

    internal static byte[] Capture(Monitor monitor, DesktopPipe.CaptureQuality? quality = null, DesktopPipe.FrameChanges? changes = null)
    {
        if (!ConsoleAvailable() || !CGPreflightScreenCaptureAccess()) throw new IOException(PortalText.Get("RemoteSupport.DesktopUnavailable"));
        var image = CGDisplayCreateImage(uint.Parse(monitor.Id, System.Globalization.CultureInfo.InvariantCulture));
        if (image == 0) throw new IOException(PortalText.Get("RemoteSupport.CaptureDisplayFailed"));
        nint colors = 0, context = 0;
        try
        {
            var sourceWidth = (long)CGImageGetWidth(image); var sourceHeight = (long)CGImageGetHeight(image);
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth * sourceHeight > MaximumDisplayPixels) throw new IOException(PortalText.Get("RemoteSupport.UnsupportedDisplaySize"));
            quality ??= DesktopPipe.Quality("balanced");
            var scale = Math.Min(1d, (double)quality.MaxWidth / sourceWidth);
            var width = Math.Max(1, (int)(sourceWidth * scale)); var height = Math.Max(1, (int)(sourceHeight * scale));
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            colors = CGColorSpaceCreateDeviceRGB();
            context = CGBitmapContextCreate(bitmap.GetPixels(), (nuint)width, (nuint)height, 8, (nuint)bitmap.RowBytes, colors, LittleEndianPremultipliedFirst);
            if (context == 0) throw new IOException(PortalText.Get("RemoteSupport.CaptureSurfaceFailed"));
            CGContextDrawImage(context, new Rect { Width = width, Height = height }, image);
            // Draw a visible pointer in display-relative coordinates; input uses layout points,
            // independently of Retina backing pixels and the JPEG scale.
            var cursor = CGEventCreate(0);
            if (cursor != 0)
            {
                try
                {
                    var point = CGEventGetLocation(cursor);
                    var x = (float)((point.X - monitor.Left) * width / monitor.Width);
                    var y = (float)((point.Y - monitor.Top) * height / monitor.Height);
                    using var canvas = new SKCanvas(bitmap);
                    using var path = new SKPath(); path.MoveTo(x, y); path.LineTo(x + 4, y + 16); path.LineTo(x + 8, y + 10); path.LineTo(x + 15, y + 9); path.Close();
                    using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true }; canvas.DrawPath(path, fill);
                    using var outline = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 }; canvas.DrawPath(path, outline);
                }
                finally { CFRelease(cursor); }
            }
            if (changes is not null && !changes.ShouldSend(bitmap.GetPixels(), bitmap.RowBytes, width * 4, height,
                $"{monitor.Id}:{monitor.Left}:{monitor.Top}:{monitor.Width}:{monitor.Height}:{sourceWidth}:{sourceHeight}:{width}:{height}:{quality.JpegQuality}")) return [];
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality.JpegQuality);
            if (encoded is null || encoded.Size > DesktopPipe.MaxFrameBytes) throw new IOException(PortalText.Get("RemoteSupport.FrameTooLarge"));
            return encoded.ToArray();
        }
        finally { if (context != 0) CFRelease(context); if (colors != 0) CFRelease(colors); CFRelease(image); }
    }

    [DllImport(CG)] internal static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(CG)] internal static extern nint CGEventCreateMouseEvent(nint source, uint kind, Point location, uint button);
    [DllImport(CG)] internal static extern void CGEventKeyboardSetUnicodeString(nint value, nuint length, [In] char[] text);
    [DllImport(CG)] internal static extern void CGEventSetFlags(nint value, ulong flags);
    [DllImport(CG)] internal static extern void CGEventSetIntegerValueField(nint value, uint field, long number);
    [DllImport(CG)] internal static extern void CGEventPost(uint tap, nint value);
    // wheel1 is the fourth fixed int32 argument on both Intel and Apple silicon.
    // With one wheel, no variadic arguments are needed.
    [DllImport(CG, EntryPoint = "CGEventCreateScrollWheelEvent")]
    internal static extern nint Scroll(nint source, uint units, uint axes, int delta);
}
