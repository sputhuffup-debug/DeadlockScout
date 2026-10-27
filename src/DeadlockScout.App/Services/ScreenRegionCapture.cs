using System.Runtime.InteropServices;
using DeadlockScout.App.Interop;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.App.Services;

/// <summary>
/// Copies one calibrated region off the screen with a GDI BitBlt of exactly that rectangle: never the whole screen or
/// window, and nothing from the game process. Our own windows (overlay, calibration tool) set
/// WDA_EXCLUDEFROMCAPTURE, so they never appear in the pixels.
/// </summary>
public sealed class ScreenRegionCapture : IRegionCapture
{
    private readonly Func<NativeMethods.RECT?> _bounds;

    /// <param name="bounds">The area regions are fractions of, in screen pixels: the game's client area, or the calibration surface.</param>
    public ScreenRegionCapture(Func<NativeMethods.RECT?> bounds) => _bounds = bounds;

    public bool IsAvailable => _bounds() is not null;

    public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct) => Task.FromResult(Capture(region));

    public CapturedFrame? Capture(HudRegion region)
    {
        if (_bounds() is not { } b || b.Width <= 0 || b.Height <= 0) return null;
        var (x, y, w, h) = region.ToPixels(b.Width, b.Height);
        return CaptureScreen(b.Left + x, b.Top + y, w, h);
    }

    /// <summary>BitBlt of one screen rectangle into a top-down 32-bit DIB. Null if any GDI call fails.</summary>
    public static CapturedFrame? CaptureScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = NativeMethods.CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) return null;
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative: top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = NativeMethods.CreateDIBSection(mem, ref header, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = NativeMethods.SelectObject(mem, dib);
            if (!NativeMethods.BitBlt(mem, 0, 0, width, height, screen, x, y, NativeMethods.SRCCOPY)) return null;

            int stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF; // GDI leaves alpha undefined
            return new CapturedFrame(width, height, stride, pixels, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (old != IntPtr.Zero) NativeMethods.SelectObject(mem, old);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (mem != IntPtr.Zero) NativeMethods.DeleteDC(mem);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
