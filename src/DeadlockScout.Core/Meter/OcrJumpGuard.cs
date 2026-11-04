using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Core.Meter;

/// <summary>
/// Turns the HUD's soul counter, read by OCR, into your net worth, and keeps other players' counters out of the meter.
/// <para>
/// The HUD counter is your <em>unspent</em> souls: it falls by an item's price when you buy one. The meter and the pace
/// curves count net worth (held items plus unspent souls), so every reading sent on is your unspent souls plus the cost
/// of the items the broadcast last said you hold, plus what you bought since that broadcast (item prices only change
/// with a patch, so a purchase shows as a drop of about one price). The broadcast catches up about 35 s later and the
/// purchases it covers move into the items' cost.
/// </para>
/// <para>
/// While you are dead the game spectates a teammate, and the counter shows theirs. Your own counter moves by small steps
/// a second, so a reading that jumps away from the last trusted one (up by more than <c>jumpSouls</c> plus
/// <c>soulsPerSecond</c> a second, or down by more than a misread without being a purchase) is held back. A drop of
/// about an item's price, read twice, is a purchase and goes through at once. A held reading is settled the first way
/// that applies:
/// </para>
/// <list type="bullet">
/// <item>the readings come back to your line (you respawned, or cycled back to yourself): every held one is dropped;
/// coming back to where you were before a recent "purchase" undoes it (that drop was a teammate's counter);</item>
/// <item>the delayed broadcast reaches its game time: it is kept when your unspent souls in the broadcast (net worth
/// less held items) match it, or moved by about as much since the trusted reading (an urn, a big kill, souls lost at a
/// death), dropped when not;</item>
/// <item>no broadcast covers it within <c>maxHold</c> of real time (the PC's clock, never the readings' own game clock,
/// which a misread could move by minutes in one reading): it is kept, so a real jump is never lost.</item>
/// </list>
/// Pure apart from logging, so the rules are unit tested.
/// </summary>
public sealed class OcrJumpGuard
{
    /// <summary>A reading this much below the last trusted one is still a misread, not someone else's counter.</summary>
    public const int MisreadDrop = 50;
    /// <summary>The default jump allowance: a reading may rise this much plus <see cref="DefaultSoulsPerSecond"/> a second.</summary>
    public const int DefaultJump = 500;
    public const double DefaultSoulsPerSecond = 40;
    /// <summary>
    /// Readings that start this early in the match count from no items held, before any broadcast: nobody owns one yet
    /// unless the app saw it bought. A reading from later with no broadcast before it is not sent on (the items are unknown).
    /// </summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);
    /// <summary>How long after a purchase coming back to the old line still undoes it.</summary>
    public static readonly TimeSpan UndoWithin = TimeSpan.FromSeconds(90);
    /// <summary>How far above the old line coming back may be (assists while dead): further is souls earned, not an undo.</summary>
    public const int UndoMargin = 200;

    private readonly int _jump;
    private readonly double _perSecond;
    private readonly TimeSpan _maxHold;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly Func<IEnumerable<int>>? _prices;
    private readonly List<DateTimeOffset> _heldAt = new(); // when each held reading arrived, by the PC's clock
    private readonly List<SoulSample> _held = new();
    private readonly List<FeedPoint> _feed = new();
    private readonly List<(TimeSpan At, int Amount)> _spent = new();
    private readonly List<(TimeSpan At, int NetWorth, int Haunted)> _sent = new();
    private readonly List<int> _agreement = new();
    // Readings with haunted souls, compared twice: counting them in net worth, and leaving them out.
    private readonly List<int> _hauntedCounted = new();
    private readonly List<int> _hauntedLeftOut = new();
    /// <summary>Readings with haunted souls needed before saying whether the broadcast counts them.</summary>
    public const int HauntedVerdictAfter = 10;
    private int[] _purchaseSizes = Array.Empty<int>();
    private SoulSample? _trusted;
    private SoulSample? _beforePurchase;
    private DateTimeOffset _purchasedAt;
    private TimeSpan? _firstReading;
    private bool _toldNoItems;

    private readonly record struct FeedPoint(TimeSpan At, int NetWorth, int Unspent)
    {
        public int Items => NetWorth - Unspent;
    }

    /// <param name="prices">Every shop item's price (the assets list, per patch): a drop of about one, two together, or
    /// one less another (an upgrade from a component) is a purchase. Without it any drop read twice is.</param>
    public OcrJumpGuard(int jumpSouls = DefaultJump, double soulsPerSecond = DefaultSoulsPerSecond, TimeSpan? maxHold = null, ILogger? log = null,
                        TimeProvider? clock = null, Func<IEnumerable<int>>? prices = null)
    {
        _clock = clock ?? TimeProvider.System;
        _jump = jumpSouls;
        _perSecond = soulsPerSecond;
        _maxHold = maxHold ?? TimeSpan.FromSeconds(120);
        _log = log ?? NullLogger.Instance;
        _prices = prices;
    }

    /// <summary>The last HUD reading trusted as yours (unspent souls).</summary>
    public SoulSample? Trusted => _trusted;
    public int HeldCount => _held.Count;
    /// <summary>Readings dropped as someone else's or as misreads, this match.</summary>
    public int Dropped { get; private set; }
    /// <summary>Purchases seen on the HUD this match (drops of about an item's price), less the ones undone.</summary>
    public int Purchases => _purchaseCount;
    private int _purchaseCount;

    /// <summary>
    /// How close the net worth sent on was to the broadcast's, once it caught up: "within 120 souls of the broadcast
    /// (median of 312 readings, 9 in 10 within 400)". Null before any comparison.
    /// </summary>
    public string? AgreementLine
    {
        get
        {
            int[] plain, counted, leftOut;
            lock (_agreement) // Diagnostics reads it from the UI thread
            {
                plain = _agreement.OrderBy(x => x).ToArray();
                counted = _hauntedCounted.OrderBy(x => x).ToArray();
                leftOut = _hauntedLeftOut.OrderBy(x => x).ToArray();
            }
            if (plain.Length == 0 && counted.Length == 0) return null;
            string line = plain.Length == 0 ? "no readings without haunted souls compared yet"
                : $"within {Median(plain):N0} souls of the broadcast (median of {plain.Length:N0} readings, 9 in 10 within {plain[Math.Min(plain.Length - 1, (int)(plain.Length * 0.9))]:N0})";
            if (counted.Length > 0)
                line += $"; carrying haunted souls ({counted.Length:N0} readings): within {Median(counted):N0} counting them, {Median(leftOut):N0} leaving them out"
                        + (HauntedVerdict is { } counts ? counts ? ", so the broadcast counts them" : ", so the broadcast leaves them out" : "");
            return line;
        }
    }

    /// <summary>
    /// Whether the broadcast's net worth counts haunted souls, from the readings that carried them: true when net worth
    /// with them agrees with the broadcast at least twice as closely as without, false the other way round, null before
    /// <see cref="HauntedVerdictAfter"/> readings or when neither is clearly closer.
    /// </summary>
    public bool? HauntedVerdict
    {
        get
        {
            int[] counted, leftOut;
            lock (_agreement) { counted = _hauntedCounted.OrderBy(x => x).ToArray(); leftOut = _hauntedLeftOut.OrderBy(x => x).ToArray(); }
            if (counted.Length < HauntedVerdictAfter) return null;
            int c = Median(counted), o = Median(leftOut);
            return c * 2 <= o ? true : o * 2 <= c ? false : null;
        }
    }

    private static int Median(int[] sorted) => sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];

    /// <summary>
    /// A HUD reading (<see cref="SoulSample.Souls"/> is the counter, your unspent souls): returns what to send to the
    /// meter now, as net worth (nothing while it is held).
    /// </summary>
    public IReadOnlyList<SoulSample> OfferOcr(SoulSample s)
    {
        _firstReading ??= s.GameTime;
        if (_trusted is null || Fits(_trusted, s))
        {
            DropHeld("that left your soul line", s);
            _trusted = s;
            return Send(new[] { s });
        }

        // Back where you were before a recent purchase: that drop was a teammate's counter (or a refund), not yours.
        // Dead, your own counter barely moves, so it must come back to within a little of where it was.
        if (_beforePurchase is { } before && _clock.GetUtcNow() - _purchasedAt <= UndoWithin && _spent.Count > 0
            && s.Souls - before.Souls is >= -MisreadDrop and <= UndoMargin)
        {
            var undone = _spent[^1];
            _spent.RemoveAt(_spent.Count - 1);
            _purchaseCount--;
            _log.LogInformation("OCR: souls came back to {Souls:N0} at {At}; the {Amount:N0} drop at {From} was not a purchase",
                s.Souls, PaceMath.FormatClock(s.GameTime), undone.Amount, PaceMath.FormatClock(undone.At));
            _beforePurchase = null;
            DropHeld("while the counter was off your line", s);
            _trusted = s;
            return Send(new[] { s });
        }

        var now = _clock.GetUtcNow();
        // A purchase: two readings in a row about one price below the trusted one, straight after it (a drop after
        // readings already held is the camera moving between teammates, not a purchase).
        if (_held.Count == 1 && s.Souls < _trusted.Souls - MisreadDrop)
        {
            var first = _held[^1];
            int drop = _trusted.Souls - first.Souls;
            if (first.Souls < _trusted.Souls - MisreadDrop && Fits(first, s, sinceLast: false)
                && PurchaseOf(drop, (first.GameTime - _trusted.GameTime).TotalSeconds) is { } price)
            {
                _held.RemoveAt(_held.Count - 1); _heldAt.RemoveAt(_heldAt.Count - 1);
                DropHeld("before a purchase", first);
                // The price, not the drop: souls earned between the readings are yours on top of it.
                _spent.Add((first.GameTime, price));
                _purchaseCount++;
                _beforePurchase = _trusted;
                _purchasedAt = now;
                _log.LogDebug("OCR: spent {Price:N0} at {At}", price, PaceMath.FormatClock(first.GameTime));
                _trusted = s;
                return Send(new[] { first, s });
            }
        }

        _held.Add(s);
        _heldAt.Add(now);
        // Real time, not the readings' clocks: one misread clock ("22:52" for 12:52) would otherwise end the hold at once
        // and let a teammate's souls through as yours.
        if (now - _heldAt[0] < _maxHold) return Array.Empty<SoulSample>();
        _log.LogInformation("OCR: kept {Count} held readings from {From}; no broadcast to check them against within {Hold} s",
            _held.Count, PaceMath.FormatClock(_held[0].GameTime), _maxHold.TotalSeconds);
        var all = _held.ToList();
        _held.Clear(); _heldAt.Clear();
        _trusted = all[^1];
        return Send(all);
    }

    /// <summary>
    /// Your own net worth from the broadcast (delayed, but never anyone else's), with <see cref="SoulSample.Unspent"/>
    /// set when the cost of the items you hold is known (without it the sample is ignored). Settles the held readings it
    /// now covers; returns the ones kept, in order, as net worth for the meter.
    /// </summary>
    public IReadOnlyList<SoulSample> OfferFeed(SoulSample f)
    {
        if (f.Unspent is not { } unspent) return Array.Empty<SoulSample>();
        var point = new FeedPoint(f.GameTime, f.Souls, unspent);
        int at = _feed.FindLastIndex(x => x.At <= f.GameTime);
        if (at >= 0 && _feed[at].At == f.GameTime) _feed[at] = point; else _feed.Insert(at + 1, point);
        // Ten minutes is far more than the broadcast's delay plus the longest hold.
        var cutoff = _feed[^1].At - TimeSpan.FromMinutes(10);
        int drop = 0;
        while (drop < _feed.Count - 1 && _feed[drop].At < cutoff) drop++;
        if (drop > 0) _feed.RemoveRange(0, drop);
        _spent.RemoveAll(x => x.At < cutoff);
        Compare(point);

        if (_held.Count == 0 || _trusted is null) return Array.Empty<SoulSample>();
        var latest = _feed[^1].At;
        List<SoulSample>? kept = null;
        int settled = 0, dropped = 0;
        // A reading's clock is whole seconds, so the broadcast is read a second either side of it.
        while (_held.Count > 0 && _held[0].GameTime + Pad <= latest)
        {
            var h = _held[0];
            _held.RemoveAt(0); _heldAt.RemoveAt(0);
            settled++;
            int ocrDelta = h.Souls - _trusted.Souls;
            // The broadcast's change between the two readings, read a second either way at each end since a reading's
            // clock is whole seconds: the narrowest and the widest it can be.
            if (UnspentAt(_trusted.GameTime - Pad) is not { } outerFrom || UnspentAt(h.GameTime + Pad) is not { } outerTo
                || UnspentAt(_trusted.GameTime + Pad) is not { } innerFrom || UnspentAt(h.GameTime - Pad) is not { } innerTo
                || UnspentAt(h.GameTime) is not { } there)
            {
                // The broadcast started after the trusted reading: nothing to compare with, so keep it like the timeout would.
                (kept ??= new()).Add(h);
                _trusted = h;
                continue;
            }
            double tolerance = Math.Max(150, 0.3 * Math.Abs(ocrDelta));
            bool sameMove = ocrDelta >= innerTo - innerFrom - tolerance && ocrDelta <= outerTo - outerFrom + tolerance;
            // The same number too: right after a misread was trusted, the moves can disagree while the value is yours.
            bool sameValue = Math.Abs(h.Souls - there) <= Math.Max(150, 0.05 * h.Souls);
            if (sameMove || sameValue)
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
        return kept is null ? Array.Empty<SoulSample>() : Send(kept);
    }

    /// <summary>Starts over for a new match.</summary>
    public void Reset()
    {
        _held.Clear(); _heldAt.Clear();
        _feed.Clear();
        _spent.Clear();
        _sent.Clear();
        lock (_agreement) { _agreement.Clear(); _hauntedCounted.Clear(); _hauntedLeftOut.Clear(); }
        _trusted = null;
        _beforePurchase = null;
        _firstReading = null;
        _toldNoItems = false;
        _purchaseCount = 0;
        Dropped = 0;
        _purchaseSizes = PurchaseSizes(_prices?.Invoke());
    }

    /// <summary>Logs how the match went: purchases seen, readings dropped, and how close the net worth was to the broadcast.</summary>
    public void Finish()
    {
        if (_firstReading is null) return;
        _log.LogInformation("OCR: {Purchases} purchases seen on the HUD, {Dropped} readings dropped; net worth {Agreement}",
            _purchaseCount, Dropped, AgreementLine ?? "never compared with the broadcast");
    }

    private static readonly TimeSpan Pad = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The default rule on its own, for the calibration tool: a soul count may move from <paramref name="from"/> to
    /// <paramref name="to"/> in <paramref name="seconds"/> (at most five counted) if it does not drop by more than a
    /// misread and does not jump by more than 500 plus 40 a second.
    /// </summary>
    public static bool Plausible(int from, int to, double seconds)
    {
        double dt = Math.Clamp(seconds, 0, 5);
        int diff = to - from;
        return diff >= -MisreadDrop && diff <= DefaultJump + DefaultSoulsPerSecond * dt;
    }

    /// <summary>
    /// The drops a purchase can make, from the shop's prices: one item, two bought together, or an upgrade that hands
    /// back a component's price. Empty without prices.
    /// </summary>
    public static int[] PurchaseSizes(IEnumerable<int>? prices)
    {
        var p = prices?.Where(x => x > 0).Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<int>();
        var sizes = new SortedSet<int>(p);
        for (int i = 0; i < p.Length; i++)
            for (int j = i; j < p.Length; j++)
            {
                sizes.Add(p[i] + p[j]);
                if (p[j] > p[i]) sizes.Add(p[j] - p[i]);
            }
        return sizes.ToArray();
    }

    /// <summary>What a drop of <paramref name="drop"/> souls over <paramref name="seconds"/> bought, or null when no price fits.</summary>
    private int? PurchaseOf(int drop, double seconds)
    {
        if (_purchaseSizes.Length == 0) _purchaseSizes = PurchaseSizes(_prices?.Invoke());
        if (_purchaseSizes.Length == 0) return drop; // no price list: any drop read twice
        // Souls earned between the two readings make the drop smaller than the price.
        double earned = MisreadDrop + _perSecond * Math.Clamp(seconds, 0, 5);
        int? best = null;
        foreach (var size in _purchaseSizes)
        {
            double shortBy = size - drop;
            if (shortBy >= -MisreadDrop && shortBy <= earned && (best is null || Math.Abs(size - drop) < Math.Abs(best.Value - drop))) best = size;
        }
        return best;
    }

    private void DropHeld(string why, SoulSample now)
    {
        if (_held.Count == 0) return;
        _log.LogInformation("OCR: dropped {Count} readings from {From} to {To} {Why} ({Min:N0}–{Max:N0} against {Souls:N0})",
            _held.Count, PaceMath.FormatClock(_held[0].GameTime), PaceMath.FormatClock(_held[^1].GameTime), why,
            _held.Min(h => h.Souls), _held.Max(h => h.Souls), now.Souls);
        Dropped += _held.Count;
        _held.Clear(); _heldAt.Clear();
    }

    private bool Fits(SoulSample trusted, SoulSample s, bool sinceLast = true)
    {
        // Steady farming adds up over a gap between readings (the game was alt-tabbed), so the allowance grows with the
        // time since the last reading of any kind, held ones included, up to five seconds: a teammate's counter must not
        // become believable just because you have been dead a while.
        var last = sinceLast && _held.Count > 0 ? _held[^1].GameTime : trusted.GameTime;
        double dt = Math.Clamp((s.GameTime - last).TotalSeconds, 0, 5);
        int diff = s.Souls - trusted.Souls;
        return diff >= -MisreadDrop && diff <= _jump + _perSecond * dt;
    }

    /// <summary>HUD readings (unspent souls) as net worth; a reading whose items are unknown is not sent.</summary>
    private IReadOnlyList<SoulSample> Send(IEnumerable<SoulSample> readings)
    {
        List<SoulSample>? list = null;
        foreach (var r in readings)
        {
            if (NetWorth(r) is not { } nw)
            {
                if (!_toldNoItems)
                {
                    _toldNoItems = true;
                    _log.LogInformation("OCR: your souls wait for the broadcast to say which items you hold (the match was under way when reading started)");
                }
                continue;
            }
            (list ??= new()).Add(new SoulSample(r.GameTime, nw, SampleSource.Ocr, r.Souls, r.Haunted));
            _sent.Add((r.GameTime, nw, r.Haunted ?? 0));
        }
        if (_sent.Count > 600) _sent.RemoveRange(0, _sent.Count - 300);
        return (IReadOnlyList<SoulSample>?)list ?? Array.Empty<SoulSample>();
    }

    /// <summary>
    /// Unspent souls plus the items the last broadcast at or before the reading says you hold, plus the purchases seen
    /// on the HUD after that broadcast. Null when no broadcast comes before it and the match was already under way.
    /// </summary>
    private int? NetWorth(SoulSample reading)
    {
        int i = _feed.FindLastIndex(x => x.At <= reading.GameTime);
        TimeSpan since;
        int items;
        if (i >= 0)
        {
            since = _feed[i].At;
            items = _feed[i].Items;
        }
        else if (_firstReading is { } first && first <= StartGrace)
        {
            since = TimeSpan.MinValue;
            items = 0;
        }
        else return null;
        int bought = 0;
        foreach (var (at, amount) in _spent)
            if (at > since && at <= reading.GameTime) bought += amount;
        return reading.Souls + items + bought;
    }

    /// <summary>Compares the broadcast's net worth with what was sent on for the same second, once it catches up.</summary>
    private void Compare(FeedPoint f)
    {
        int i = _sent.FindLastIndex(x => x.At <= f.At);
        if (i < 0 || f.At - _sent[i].At > Pad) return;
        var (_, nw, haunted) = _sent[i];
        lock (_agreement)
        {
            if (haunted > 0)
            {
                _hauntedCounted.Add(Math.Abs(nw - f.NetWorth));
                _hauntedLeftOut.Add(Math.Abs(nw - haunted - f.NetWorth));
                if (_hauntedCounted.Count > 5000) { _hauntedCounted.RemoveRange(0, 1000); _hauntedLeftOut.RemoveRange(0, 1000); }
            }
            else
            {
                _agreement.Add(Math.Abs(nw - f.NetWorth));
                if (_agreement.Count > 5000) _agreement.RemoveRange(0, 1000);
            }
        }
    }

    /// <summary>Your broadcast unspent souls at a game time, interpolated; null outside what the broadcast has covered.</summary>
    private double? UnspentAt(TimeSpan t)
    {
        if (_feed.Count == 0 || t < _feed[0].At || t > _feed[^1].At) return null;
        int i = _feed.FindIndex(x => x.At >= t);
        var b = _feed[i];
        if (b.At == t || i == 0) return b.Unspent;
        var a = _feed[i - 1];
        double f = (t - a.At).Ticks / (double)(b.At - a.At).Ticks;
        return a.Unspent + (b.Unspent - a.Unspent) * f;
    }
}
