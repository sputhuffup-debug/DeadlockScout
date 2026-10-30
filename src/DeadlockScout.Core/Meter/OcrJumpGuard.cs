using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.Meter;

/// <summary>
/// Keeps other players' souls out of the meter. While you are dead the game spectates a teammate, and the HUD's soul
/// counter shows theirs: OCR reads it like any other number, and cycling between teammates made the souls per minute
/// jump to each one's pace (and, since the meter ignores a lower reading as a misread, stall after you respawned until
/// you passed the richest one).
/// <para>
/// Your own souls move by small steps a second, so a reading that jumps away from the last trusted one (by more than
/// <c>jumpSouls</c> plus <c>soulsPerSecond</c> a second, or down by more than a misread) is held back, not sent on.
/// A held reading is settled the first way that applies:
/// </para>
/// <list type="bullet">
/// <item>the readings come back to your line (you respawned, or cycled back to yourself): every held one is dropped;</item>
/// <item>the delayed broadcast reaches its game time: it is kept when your own souls in the broadcast moved by about as
/// much since the trusted reading (an urn, a big kill), dropped when they did not;</item>
/// <item>no broadcast covers it within <c>maxHold</c>: it is kept, so a real jump is never lost.</item>
/// </list>
/// Only differences are compared with the broadcast, never the numbers themselves. Pure apart from logging, so the
/// rules are unit tested.
/// </summary>
public sealed class OcrJumpGuard
{
    /// <summary>A reading this much below the last trusted one is still a misread, not someone else's counter.</summary>
    public const int MisreadDrop = 50;

    private readonly int _jump;
    private readonly double _perSecond;
    private readonly TimeSpan _maxHold;
    private readonly ILogger _log;
    private readonly List<SoulSample> _held = new();
    private readonly List<SoulSample> _feed = new();
    private SoulSample? _trusted;

    public OcrJumpGuard(int jumpSouls = 500, double soulsPerSecond = 40, TimeSpan? maxHold = null, ILogger? log = null)
    {
        _jump = jumpSouls;
        _perSecond = soulsPerSecond;
        _maxHold = maxHold ?? TimeSpan.FromSeconds(120);
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>The last reading sent on as yours.</summary>
    public SoulSample? Trusted => _trusted;
    public int HeldCount => _held.Count;
    /// <summary>Readings dropped as someone else's, this match.</summary>
    public int Dropped { get; private set; }

    /// <summary>An OCR reading: returns what to send to the meter now (nothing while it is held).</summary>
    public IReadOnlyList<SoulSample> OfferOcr(SoulSample s)
    {
        if (_trusted is null || Fits(_trusted, s))
        {
            if (_held.Count > 0)
            {
                _log.LogInformation("OCR: dropped {Count} readings from {From} to {To} that left your soul line ({Min:N0}–{Max:N0} against {Souls:N0}); spectating while dead?",
                    _held.Count, PaceMath.FormatClock(_held[0].GameTime), PaceMath.FormatClock(_held[^1].GameTime),
                    _held.Min(h => h.Souls), _held.Max(h => h.Souls), _trusted!.Souls);
                Dropped += _held.Count;
                _held.Clear();
            }
            _trusted = s;
            return new[] { s };
        }
        _held.Add(s);
        if (s.GameTime - _held[0].GameTime < _maxHold) return Array.Empty<SoulSample>();
        _log.LogInformation("OCR: kept {Count} held readings from {From}; no broadcast to check them against within {Hold} s",
            _held.Count, PaceMath.FormatClock(_held[0].GameTime), _maxHold.TotalSeconds);
        var all = _held.ToList();
        _held.Clear();
        _trusted = all[^1];
        return all;
    }

    /// <summary>
    /// Your own souls from the broadcast (delayed, but never anyone else's). Settles the held readings it now covers;
    /// returns the ones kept, in order, for the meter.
    /// </summary>
    public IReadOnlyList<SoulSample> OfferFeed(SoulSample f)
    {
        int at = _feed.FindLastIndex(x => x.GameTime <= f.GameTime);
        if (at >= 0 && _feed[at].GameTime == f.GameTime) _feed[at] = f; else _feed.Insert(at + 1, f);
        // Ten minutes is far more than the broadcast's delay plus the longest hold.
        var cutoff = _feed[^1].GameTime - TimeSpan.FromMinutes(10);
        int drop = 0;
        while (drop < _feed.Count - 1 && _feed[drop].GameTime < cutoff) drop++;
        if (drop > 0) _feed.RemoveRange(0, drop);

        if (_held.Count == 0 || _trusted is null) return Array.Empty<SoulSample>();
        var latest = _feed[^1].GameTime;
        List<SoulSample>? kept = null;
        int settled = 0, dropped = 0;
        // A reading's clock is whole seconds, so the broadcast is read a second either side of it.
        while (_held.Count > 0 && _held[0].GameTime + Pad <= latest)
        {
            var h = _held[0];
            _held.RemoveAt(0);
            settled++;
            int ocrDelta = h.Souls - _trusted.Souls;
            // The broadcast's change between the two readings, read a second either way at each end since a reading's
            // clock is whole seconds: the narrowest and the widest it can be.
            if (FeedAt(_trusted.GameTime - Pad) is not { } outerFrom || FeedAt(h.GameTime + Pad) is not { } outerTo
                || FeedAt(_trusted.GameTime + Pad) is not { } innerFrom || FeedAt(h.GameTime - Pad) is not { } innerTo)
            {
                // The broadcast started after the trusted reading: nothing to compare with, so keep it like the timeout would.
                (kept ??= new()).Add(h);
                _trusted = h;
                continue;
            }
            double tolerance = Math.Max(150, 0.3 * Math.Abs(ocrDelta));
            if (ocrDelta >= innerTo - innerFrom - tolerance && ocrDelta <= outerTo - outerFrom + tolerance)
            {
                (kept ??= new()).Add(h);
                _trusted = h;
            }
            else
            {
                dropped++;
            }
        }
        if (dropped > 0)
        {
            Dropped += dropped;
            _log.LogInformation("OCR: the broadcast says {Dropped} of {Settled} held readings were not your souls (spectating while dead?)", dropped, settled);
        }
        return (IReadOnlyList<SoulSample>?)kept ?? Array.Empty<SoulSample>();
    }

    /// <summary>Starts over for a new match.</summary>
    public void Reset()
    {
        _held.Clear();
        _feed.Clear();
        _trusted = null;
        Dropped = 0;
    }

    private static readonly TimeSpan Pad = TimeSpan.FromSeconds(1);

    private bool Fits(SoulSample trusted, SoulSample s)
    {
        // Steady farming adds up over a gap between readings (the game was alt-tabbed), so the allowance grows with the
        // time since the last reading of any kind, held ones included, up to five seconds: a teammate's counter must not
        // become believable just because you have been dead a while.
        var last = _held.Count > 0 ? _held[^1].GameTime : trusted.GameTime;
        double dt = Math.Clamp((s.GameTime - last).TotalSeconds, 0, 5);
        int diff = s.Souls - trusted.Souls;
        return diff >= -MisreadDrop && diff <= _jump + _perSecond * dt;
    }

    /// <summary>Your broadcast souls at a game time, interpolated; null outside what the broadcast has covered.</summary>
    private double? FeedAt(TimeSpan t)
    {
        if (_feed.Count == 0 || t < _feed[0].GameTime || t > _feed[^1].GameTime) return null;
        int i = _feed.FindIndex(x => x.GameTime >= t);
        var b = _feed[i];
        if (b.GameTime == t || i == 0) return b.Souls;
        var a = _feed[i - 1];
        double f = (t - a.GameTime).Ticks / (double)(b.GameTime - a.GameTime).Ticks;
        return a.Souls + (b.Souls - a.Souls) * f;
    }
}
