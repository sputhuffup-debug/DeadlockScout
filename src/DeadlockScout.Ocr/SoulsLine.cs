using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>
/// Cuts the souls box down to the souls number before it is read. The HUD draws more than the number there: a small
/// "SOULS" label to its right (Windows OCR read it as "501", so 737 came out as "737,501"), and, while you carry haunted
/// souls, a second, smaller line under it ("1,051 HAUNTED SOULS") with the number moved up to make room. So the box is
/// drawn tall enough for both positions, and this keeps only the line with the tallest characters, from its first
/// full-height character to its last: the label and the haunted line are smaller and fall away; a comma between digits
/// stays. Anything it cannot make sense of (no ink, one line only) is returned as it was. Pure, so tested on synthetic
/// frames.
/// </summary>
public static class SoulsLine
{
    /// <summary>A character at least this share of the line's height is part of the number; the label is about half.</summary>
    public const double FullHeight = 0.7;
    /// <summary>Pixels kept around the number.</summary>
    public const int Pad = 2;

    /// <summary>A line under the number this share of its height, or between, may be the haunted souls line.</summary>
    public const double BelowMin = 0.35, BelowMax = 0.95;

    /// <summary>The souls count only.</summary>
    public static CapturedFrame Isolate(CapturedFrame crop) => Split(crop).Number;

    /// <summary>A rectangle of a crop, in pixels, corners included.</summary>
    public readonly record struct PixelRect(int X0, int Y0, int X1, int Y1);

    /// <summary>The souls count, and the smaller line under it (the haunted souls, whole width) when the box holds one.</summary>
    public static (CapturedFrame Number, CapturedFrame? Below) Split(CapturedFrame crop)
    {
        var (number, below) = Locate(crop);
        var n = number is { } r && !(r.X0 == 0 && r.Y0 == 0 && r.X1 == crop.Width - 1 && r.Y1 == crop.Height - 1)
            ? Cut(crop, r.X0, r.X1, r.Y0, r.Y1) : crop;
        return (n, below is { } b ? Cut(crop, b.X0, b.X1, b.Y0, b.Y1) : null);
    }

    /// <summary>
    /// Where <see cref="Split"/> cuts: the count's rectangle and the haunted line's, in the crop's pixels; null for one
    /// it did not find (the count's is then the whole crop when read). For the calibration tool's outline.
    /// </summary>
    public static (PixelRect? Number, PixelRect? Below) Locate(CapturedFrame crop)
    {
        int w = crop.Width, h = crop.Height;
        // A frame whose pixels do not fill its size is passed on as it is: never an exception in the read loop.
        if (w <= 0 || h <= 0 || crop.Stride < w * 4 || crop.Bgra.Length < (long)crop.Stride * (h - 1) + w * 4) return (null, null);
        var ink = GlyphSegmenter.Ink(crop);

        // Bands of rows with ink, split at rows with none.
        var bands = new List<(int Y0, int Y1, int Ink)>();
        int start = -1, count = 0;
        for (int y = 0; y <= h; y++)
        {
            int row = 0;
            if (y < h) for (int x = 0; x < w; x++) if (ink[y * w + x]) row++;
            if (row > 0) { if (start < 0) { start = y; count = 0; } count += row; }
            else if (start >= 0) { bands.Add((start, y - 1, count)); start = -1; }
        }
        bands.RemoveAll(b => b.Ink < GlyphSegmenter.MinInk);
        if (bands.Count == 0) return (null, null);
        var line = bands.OrderByDescending(b => b.Y1 - b.Y0).ThenByDescending(b => b.Ink).First();
        int lineHeight = line.Y1 - line.Y0 + 1;
        PixelRect? below = null;
        foreach (var b in bands)
        {
            if (b.Y0 <= line.Y1) continue;
            int bh = b.Y1 - b.Y0 + 1;
            if (bh >= BelowMin * lineHeight && bh <= BelowMax * lineHeight)
            {
                below = new PixelRect(0, Math.Max(line.Y1 + 1, b.Y0 - Pad), w - 1, Math.Min(h - 1, b.Y1 + Pad));
                break;
            }
        }

        // Within that band, the runs of columns with ink and how tall each one is.
        int left = -1, right = -1;
        int runStart = -1, top = h, bottom = -1;
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w)
                for (int y = line.Y0; y <= line.Y1; y++)
                    if (ink[y * w + x]) { any = true; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            if (any && runStart < 0) runStart = x;
            else if (!any && runStart >= 0)
            {
                if (bottom - top + 1 >= FullHeight * lineHeight)
                {
                    if (left < 0) left = runStart;
                    right = x - 1;
                }
                runStart = -1; top = h; bottom = -1;
            }
        }
        if (left < 0) return (null, below);
        return (new PixelRect(Math.Max(0, left - Pad), Math.Max(0, line.Y0 - Pad), Math.Min(w - 1, right + Pad), Math.Min(h - 1, line.Y1 + Pad)), below);
    }

    /// <summary>
    /// The crop with what is read outlined, for the calibration tool's preview: the count in blue, the soul icon that is
    /// skipped in grey (once <paramref name="souls"/> has learned it), the haunted souls line in red.
    /// </summary>
    public static CapturedFrame Outline(CapturedFrame crop, GlyphSet? souls)
    {
        var (number, below) = Locate(crop);
        if (number is null && below is null) return crop;
        var px = crop.Bgra.ToArray();
        void Box(PixelRect r, byte b, byte g, byte red)
        {
            void Dot(int x, int y)
            {
                if (x < 0 || y < 0 || x >= crop.Width || y >= crop.Height) return;
                int i = y * crop.Stride + x * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = red; px[i + 3] = 255;
            }
            for (int x = r.X0; x <= r.X1; x++) { Dot(x, r.Y0); Dot(x, r.Y1); }
            for (int y = r.Y0; y <= r.Y1; y++) { Dot(r.X0, y); Dot(r.X1, y); }
        }
        if (number is { } n)
        {
            var count = n;
            if (souls?.IconSpan(Cut(crop, n.X0, n.X1, n.Y0, n.Y1)) is { } icon)
            {
                Box(new PixelRect(n.X0 + icon.X0, n.Y0, n.X0 + icon.X1, n.Y1), 0x80, 0x80, 0x80);
                count = n with { X0 = Math.Min(n.X1, n.X0 + Math.Max(icon.X1 + 1, icon.NextX0 - Pad)) };
            }
            Box(count, 0xF5, 0xA8, 0x5F);
        }
        if (below is { } h) Box(h, 0x5A, 0x5A, 0xF0);
        return crop with { Bgra = px };
    }

    internal static CapturedFrame Cut(CapturedFrame crop, int x0, int x1, int y0, int y1)
    {
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var px = new byte[cw * ch * 4];
        var src = crop.Bgra.Span;
        for (int y = 0; y < ch; y++)
            src.Slice((y0 + y) * crop.Stride + x0 * 4, cw * 4).CopyTo(px.AsSpan(y * cw * 4, cw * 4));
        return new CapturedFrame(cw, ch, cw * 4, px, crop.At);
    }
}
