namespace DeadlockScout.Ocr;

/// <summary>
/// Notices, during a match, that a box has stopped reading: the souls count or the clock not read once in
/// <see cref="After"/> of real time while the game was in front. That is a box drawn wrong (or a HUD that moved), which
/// used to show only in Diagnostics after the match; now the tray says so once a match, naming the box, so it can be
/// redrawn before the match is lost to it. Pure, so tested.
/// </summary>
public sealed class OcrReadWatch
{
    /// <summary>How long a box may go unread before the warning: long enough for the shop or a death screen.</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(45);

    private DateTimeOffset? _soulsSince, _clockSince;
    private bool _warned;

    /// <summary>
    /// One attempt to read both boxes (only while the game is in front). Returns the warning the first time a box has
    /// gone <see cref="After"/> without a reading this match, else null.
    /// </summary>
    public string? Record(bool soulsRead, bool clockRead, DateTimeOffset now)
    {
        _soulsSince = soulsRead ? null : _soulsSince ?? now;
        _clockSince = clockRead ? null : _clockSince ?? now;
        if (_warned) return null;
        bool souls = _soulsSince is { } s && now - s >= After, clock = _clockSince is { } c && now - c >= After;
        if (!souls && !clock) return null;
        _warned = true;
        string which = souls && clock ? "your souls box and the match clock box" : souls ? "your souls box" : "the match clock box";
        return $"OCR has not read {which} for {(int)After.TotalSeconds} s. Open the calibration tool from the tray and redraw it; until then your pace comes from the broadcast.";
    }

    /// <summary>A new match: the warning may come once more.</summary>
    public void Reset()
    {
        _soulsSince = _clockSince = null;
        _warned = false;
    }
}
