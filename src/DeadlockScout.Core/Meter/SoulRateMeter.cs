using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Meter;

/// <summary>What the meter knows at one moment; everything the pace strip shows comes from here plus a curve.</summary>
public sealed record MeterReading(
    TimeSpan GameTime,
    int Souls,
    /// <summary>Souls per minute over the rolling window (shorter from match start until the window is full).</summary>
    double WindowSpm,
    /// <summary>Souls per minute since match start.</summary>
    double OverallSpm,
    /// <summary>The window actually used; equals the configured window once enough data exists.</summary>
    TimeSpan WindowUsed,
    SampleSource Source,
    int SampleCount);

/// <summary>
/// Rolling souls-per-minute over a game-time window. Samples are keyed by game time, not wall time, so a
/// delayed feed and a real-time OCR source can be mixed without resetting.
/// </summary>
public sealed class SoulRateMeter
{
    private readonly List<SoulSample> _samples = new();
    private readonly TimeSpan _window;
    private readonly TimeSpan _retention;

    public SoulRateMeter(TimeSpan? window = null, TimeSpan? retention = null)
    {
        _window = window ?? TimeSpan.FromSeconds(120);
        _retention = retention ?? TimeSpan.FromMinutes(10);
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (_retention < _window) throw new ArgumentException("Retention must cover the window.", nameof(retention));
    }

    public TimeSpan Window => _window;
    public int Count => _samples.Count;
    public SoulSample? Latest => _samples.Count == 0 ? null : _samples[^1];

    /// <summary>Adds a sample, keeping the buffer sorted by game time and dropping anything older than the retention.</summary>
    public void Add(SoulSample sample)
    {
        if (sample.GameTime < TimeSpan.Zero) return;
        if (sample.Souls < 0) return;

        // A sample at the same game time replaces the previous one (OCR re-reads, feed replays).
        int idx = _samples.FindLastIndex(s => s.GameTime <= sample.GameTime);
        if (idx >= 0 && _samples[idx].GameTime == sample.GameTime)
        {
            _samples[idx] = sample;
        }
        else
        {
            // Souls never go down in Deadlock; a lower value after a higher one is a misread.
            if (idx >= 0 && sample.Souls < _samples[idx].Souls) return;
            if (idx + 1 < _samples.Count && sample.Souls > _samples[idx + 1].Souls) return;
            _samples.Insert(idx + 1, sample);
        }

        var cutoff = _samples[^1].GameTime - _retention;
        int drop = 0;
        while (drop < _samples.Count - 1 && _samples[drop].GameTime < cutoff) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
    }

    /// <summary>Resets the buffer for a new match.</summary>
    public void Clear() => _samples.Clear();

    /// <summary>Reading at the latest sample, or null before the first sample.</summary>
    public MeterReading? Read() => Latest is { } l ? ReadAt(l.GameTime) : null;

    /// <summary>Reading at an arbitrary game time within the buffered range.</summary>
    public MeterReading? ReadAt(TimeSpan t)
    {
        if (_samples.Count == 0) return null;
        var latest = _samples[^1];
        if (t > latest.GameTime) t = latest.GameTime;

        double now = InterpolateAt(t);
        var from = t - _window;
        double before;
        TimeSpan used;
        if (from <= TimeSpan.Zero)
        {
            // Not two minutes in yet: measure from match start (0 souls at 0:00).
            before = 0;
            used = t;
        }
        else
        {
            before = InterpolateAt(from);
            used = _window;
        }

        double windowSpm = used > TimeSpan.Zero ? (now - before) / used.TotalMinutes : 0;
        double overallSpm = t > TimeSpan.Zero ? now / t.TotalMinutes : 0;

        return new MeterReading(t, (int)Math.Round(now), windowSpm, overallSpm, used, latest.Source, _samples.Count);
    }

    /// <summary>Linear interpolation between the two nearest samples; clamps outside the buffered range and assumes 0 at 0:00.</summary>
    public double InterpolateAt(TimeSpan t)
    {
        if (_samples.Count == 0) return 0;
        if (t <= TimeSpan.Zero) return 0;
        if (t <= _samples[0].GameTime)
        {
            // Between match start and the first sample: assume a straight line from 0.
            var first = _samples[0];
            if (first.GameTime <= TimeSpan.Zero) return first.Souls;
            return first.Souls * (t.Ticks / (double)first.GameTime.Ticks);
        }
        if (t >= _samples[^1].GameTime) return _samples[^1].Souls;

        // Binary search for the first sample at or after t.
        int lo = 0, hi = _samples.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_samples[mid].GameTime < t) lo = mid + 1; else hi = mid;
        }
        var b = _samples[lo];
        var a = _samples[lo - 1];
        if (b.GameTime == a.GameTime) return b.Souls;
        double f = (t - a.GameTime).Ticks / (double)(b.GameTime - a.GameTime).Ticks;
        return a.Souls + (b.Souls - a.Souls) * f;
    }
}
