namespace DeadlockScout.Ocr.Abstractions;

/// <summary>A captured frame of the game window: raw BGRA32 pixels. Captures only our own screen, never the process.</summary>
public sealed record CapturedFrame(int Width, int Height, int Stride, ReadOnlyMemory<byte> Bgra, DateTimeOffset At);

/// <summary>Screen capture of the Deadlock window (Windows.Graphics.Capture in M6).</summary>
public interface ICaptureSource
{
    bool IsAvailable { get; }
    Task<CapturedFrame?> CaptureAsync(CancellationToken ct);
}

/// <summary>Crop rectangles for the soul counter and the match timer, in 1080p-relative coordinates, from settings.</summary>
public interface IHudRegionMap
{
    HudRegion? Souls { get; }
    HudRegion? Timer { get; }
}

/// <summary>Normalised rectangle (0..1 of width/height) so one calibration works at any resolution.</summary>
public sealed record HudRegion(double X, double Y, double Width, double Height)
{
    public (int X, int Y, int W, int H) ToPixels(int frameWidth, int frameHeight)
        => ((int)(X * frameWidth), (int)(Y * frameHeight), Math.Max(1, (int)(Width * frameWidth)), Math.Max(1, (int)(Height * frameHeight)));
}

/// <summary>Reads digits out of a cropped region. Windows.Media.Ocr first, Tesseract optional.</summary>
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<string?> RecognizeAsync(CapturedFrame frame, HudRegion region, CancellationToken ct);
}
