namespace DeadlockScout.Ocr.Abstractions;

/// <summary>
/// Pixels of one captured screen region: raw BGRA32, top-down. Only the calibrated rectangle is ever captured, never the
/// whole screen or window, and never anything from the game process.
/// </summary>
public sealed record CapturedFrame(int Width, int Height, int Stride, ReadOnlyMemory<byte> Bgra, DateTimeOffset At);

/// <summary>Copies one region of the game window off the screen (GDI BitBlt of just that rectangle on Windows).</summary>
public interface IRegionCapture
{
    /// <summary>False when the game window is not found.</summary>
    bool IsAvailable { get; }
    Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct);
}

/// <summary>The calibrated regions, from settings.</summary>
public interface IHudRegionMap
{
    HudRegion? Souls { get; }
    HudRegion? Timer { get; }
    /// <summary>Enemy soul counts by scoreboard row from the top; null entries are not calibrated.</summary>
    IReadOnlyList<HudRegion?> EnemySouls { get; }
}

/// <summary>A rectangle in fractions (0..1) of the game window's client area, so one calibration works at any resolution.</summary>
public sealed record HudRegion(double X, double Y, double Width, double Height)
{
    /// <summary>Smallest side a calibrated box may have, as a fraction of the window: about 2 px at 1080p.</summary>
    public const double MinSide = 0.002;

    /// <summary>Pixel rectangle inside a client area of the given size, clamped to it.</summary>
    public (int X, int Y, int W, int H) ToPixels(int clientWidth, int clientHeight)
    {
        int x = Math.Clamp((int)Math.Round(X * clientWidth), 0, Math.Max(0, clientWidth - 1));
        int y = Math.Clamp((int)Math.Round(Y * clientHeight), 0, Math.Max(0, clientHeight - 1));
        int w = Math.Clamp((int)Math.Round(Width * clientWidth), 1, Math.Max(1, clientWidth - x));
        int h = Math.Clamp((int)Math.Round(Height * clientHeight), 1, Math.Max(1, clientHeight - y));
        return (x, y, w, h);
    }

    /// <summary>
    /// The box a mouse drag from one corner to the other describes on a surface of the given size, in fractions, clamped
    /// to the surface. Null when either side is smaller than <see cref="MinSide"/> (a click, not a drag).
    /// </summary>
    public static HudRegion? FromDrag(double x1, double y1, double x2, double y2, double surfaceWidth, double surfaceHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0) return null;
        double left = Math.Clamp(Math.Min(x1, x2) / surfaceWidth, 0, 1), right = Math.Clamp(Math.Max(x1, x2) / surfaceWidth, 0, 1);
        double top = Math.Clamp(Math.Min(y1, y2) / surfaceHeight, 0, 1), bottom = Math.Clamp(Math.Max(y1, y2) / surfaceHeight, 0, 1);
        if (right - left < MinSide || bottom - top < MinSide) return null;
        return new HudRegion(left, top, right - left, bottom - top);
    }
}

/// <summary>Reads text out of one captured region. Windows.Media.Ocr first, Tesseract optional.</summary>
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct);
}
