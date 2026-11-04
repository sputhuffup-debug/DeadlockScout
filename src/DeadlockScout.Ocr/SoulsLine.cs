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

    public static CapturedFrame Isolate(CapturedFrame crop)
    {
        int w = crop.Width, h = crop.Height;
        // A frame whose pixels do not fill its size is passed on as it is: never an exception in the read loop.
        if (w <= 0 || h <= 0 || crop.Stride < w * 4 || crop.Bgra.Length < (long)crop.Stride * (h - 1) + w * 4) return crop;
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
        if (bands.Count == 0) return crop;
        var line = bands.OrderByDescending(b => b.Y1 - b.Y0).ThenByDescending(b => b.Ink).First();
        int lineHeight = line.Y1 - line.Y0 + 1;

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
        if (left < 0) return crop;

        int x0 = Math.Max(0, left - Pad), x1 = Math.Min(w - 1, right + Pad);
        int y0 = Math.Max(0, line.Y0 - Pad), y1 = Math.Min(h - 1, line.Y1 + Pad);
        if (x0 == 0 && y0 == 0 && x1 == w - 1 && y1 == h - 1) return crop;
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var px = new byte[cw * ch * 4];
        var src = crop.Bgra.Span;
        for (int y = 0; y < ch; y++)
            src.Slice((y0 + y) * crop.Stride + x0 * 4, cw * 4).CopyTo(px.AsSpan(y * cw * 4, cw * 4));
        return new CapturedFrame(cw, ch, cw * 4, px, crop.At);
    }
}
