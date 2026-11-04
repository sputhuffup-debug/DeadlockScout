using System.Globalization;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Meter;

/// <summary>A death seen on the HUD: the haunted souls it took.</summary>
public sealed record HauntedLoss(TimeSpan At, int Souls);

/// <summary>
/// What a match's soul readings say about spending, for the match recap (0.3.3): how long you sat on unspent souls
/// over the coaching threshold, and the haunted souls deaths took.
/// <list type="bullet">
/// <item>Unspent time: the game time between readings whose unspent souls were at or over the threshold, a gap counted
/// at most <see cref="MaxGap"/> (OCR off, the game alt-tabbed), with the longest stretch and when it began.</item>
/// <item>A death on the HUD: the haunted line was there (at least <see cref="MinHaunted"/>), the next reading has none,
/// and your unspent souls fell by about as much (<see cref="LossShare"/> of it or more). Secured haunted souls move
/// into the count instead and leave unspent souls where they were, so they are no loss.</item>
/// </list>
/// Fed with the soul source's samples in game-time order (OCR ones carry the haunted souls). Pure, so tested; nothing
/// here is stored past the match.
/// </summary>
public sealed class SoulsRecap
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);
    public const int MinHaunted = 100;
    public const double LossShare = 0.8;
    /// <summary>How long the pace strip names a death's loss.</summary>
    public static readonly TimeSpan ShowLossFor = TimeSpan.FromSeconds(30);

    private readonly int _threshold;
    private readonly List<HauntedLoss> _losses = new();
    private SoulSample? _last;
    private TimeSpan _over, _longest, _stretch;
    private TimeSpan? _stretchStart, _longestStart;
    private readonly object _gate = new();

    /// <param name="threshold">The unspent souls that count as sitting on them (<c>coaching.unspentSouls</c>); 0 counts none.</param>
    public SoulsRecap(int threshold) => _threshold = threshold;

    public IReadOnlyList<HauntedLoss> Losses { get { lock (_gate) return _losses.ToList(); } }

    /// <summary>One soul sample; returns the loss when this reading shows a death took haunted souls.</summary>
    public HauntedLoss? Observe(SoulSample s)
    {
        if (s.Unspent is not { } unspent) return null;
        lock (_gate)
        {
            if (_last is { } prev && s.GameTime <= prev.GameTime) return null;
            HauntedLoss? loss = null;
            if (_last is { Unspent: { } before } p)
            {
                var dt = s.GameTime - p.GameTime;
                if (dt > MaxGap) dt = MaxGap;
                bool over = _threshold > 0 && before >= _threshold;
                if (over)
                {
                    _over += dt;
                    _stretchStart ??= p.GameTime;
                    _stretch += dt;
                    if (_stretch > _longest) { _longest = _stretch; _longestStart = _stretchStart; }
                }
                if (!over || _threshold <= 0 || unspent < _threshold) { _stretch = TimeSpan.Zero; _stretchStart = null; }

                int haunted = p.Haunted ?? 0;
                if (s.Source == SampleSource.Ocr && p.Source == SampleSource.Ocr && haunted >= MinHaunted && (s.Haunted ?? 0) == 0
                    && before - unspent >= LossShare * haunted)
                {
                    loss = new HauntedLoss(s.GameTime, haunted);
                    _losses.Add(loss);
                }
            }
            _last = s;
            return loss;
        }
    }

    /// <summary>"Died at 14:20: lost 1,051 haunted souls" for <see cref="ShowLossFor"/> after a loss; null otherwise.</summary>
    public string? LossLine(TimeSpan now)
    {
        lock (_gate)
        {
            if (_losses.Count == 0 || now - _losses[^1].At > ShowLossFor || now < _losses[^1].At) return null;
            var l = _losses[^1];
            return $"Died at {PaceMath.FormatClock(l.At)}: lost {l.Souls.ToString("N0", CultureInfo.InvariantCulture)} haunted souls";
        }
    }

    /// <summary>
    /// The recap: "3k+ unspent for 6:20, longest 2:10 from 18:40 · lost 2.3k haunted souls in 3 deaths". Null when there
    /// is nothing to say (no time over the threshold and no loss).
    /// </summary>
    public string? Line()
    {
        lock (_gate)
        {
            var parts = new List<string>();
            if (_over > TimeSpan.Zero && _threshold > 0)
            {
                string limit = _threshold % 1000 == 0 ? $"{_threshold / 1000}k" : TeamSoulsLead.Short(_threshold);
                string part = $"{limit}+ unspent for {PaceMath.FormatClock(_over)}";
                if (_longestStart is { } from && _longest < _over) part += $", longest {PaceMath.FormatClock(_longest)} from {PaceMath.FormatClock(from)}";
                parts.Add(part);
            }
            if (_losses.Count > 0)
            {
                int total = _losses.Sum(l => l.Souls);
                parts.Add($"lost {TeamSoulsLead.Short(total)} haunted souls in {_losses.Count} {(_losses.Count == 1 ? "death" : "deaths")}");
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}
