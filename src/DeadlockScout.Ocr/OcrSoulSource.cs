using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Ocr.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>
/// Reads your soul counter and the match clock off the screen once a second. Real time, so it is the source that removes the
/// broadcast delay once an engine is available. Emits nothing while unhealthy, and the composite source then falls
/// back to the live feed.
/// </summary>
public sealed class OcrSoulSource : ISoulSampleSource
{
    private readonly IRegionCapture _capture;
    private readonly IHudRegionMap _regions;
    private readonly IOcrEngine _engine;
    private readonly IClock _clock;
    private readonly OcrHealth _health;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly Func<bool> _enabled;
    private readonly OcrClockGuard _clockGuard = new();
    private DateTimeOffset _clockLogged = DateTimeOffset.MinValue;
    private int _clockLoggedRejected;

    public OcrSoulSource(IRegionCapture capture, IHudRegionMap regions, IOcrEngine engine, IClock clock,
                         double minHealthy = 0.8, TimeSpan? interval = null, ILogger<OcrSoulSource>? log = null, Func<bool>? enabled = null)
    {
        _enabled = enabled ?? (() => true);
        _capture = capture;
        _regions = regions;
        _engine = engine;
        _clock = clock;
        _health = new OcrHealth(minHealthy: minHealthy);
        _interval = interval ?? DefaultInterval;
        _log = log ?? NullLogger<OcrSoulSource>.Instance;
    }

    /// <summary>The overlay refreshes once a second, so faster reads would never be seen.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    public SampleSource Kind => SampleSource.Ocr;
    public TimeSpan ExpectedLatency => TimeSpan.FromSeconds(1);
    public bool IsHealthy => _engine.IsAvailable && _capture.IsAvailable && _health.IsHealthy;
    public double SuccessRate => _health.SuccessRate;
    /// <summary>The haunted souls in the last reading (0 without that line): already counted in its souls.</summary>
    public int LastHaunted { get; private set; }

    private readonly OcrReadWatch _watch = new();
    /// <summary>Raised once a match when a box has gone unread for <see cref="OcrReadWatch.After"/>: the warning to show.</summary>
    public event Action<string>? ReadsFailing;

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // Only a missing engine ends the source. Capture comes and goes (the game not in front, minimized, not started yet)
        // and is checked on every pass, so being alt-tabbed when the match starts does not switch OCR off for the match.
        if (!_engine.IsAvailable)
        {
            _log.LogInformation("OCR source idle: engine {Engine} is not available", _engine.Name);
            yield break;
        }
        _clockGuard.Reset(); // a new match: its clock starts again at 0:00
        _watch.Reset();
        _lastTaught = null;
        _clockLoggedRejected = 0;
        while (!ct.IsCancellationRequested)
        {
            // Nothing is captured while OCR is switched off, or while the game is not on screen (the capture reports
            // unavailable then): those passes neither read nor count against health.
            if (_enabled() && _capture.IsAvailable && await TryReadOnceAsync(ct).ConfigureAwait(false) is { } sample) yield return sample;
            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Captures just the two regions and reads them; records health. Exposed for tests with fake engines.</summary>
    public async Task<SoulSample?> TryReadOnceAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_regions.Souls is not { } soulsBox || _regions.Timer is not { } timerBox) { _health.Record(now, false); return null; }
        var soulsCrop = await _capture.CaptureAsync(soulsBox, ct).ConfigureAwait(false);
        var timerCrop = await _capture.CaptureAsync(timerBox, ct).ConfigureAwait(false);
        if (soulsCrop is null || timerCrop is null) { _health.Record(now, false); return null; }

        // The souls number alone (not the "SOULS" label beside it), and the haunted souls line under it when there is one:
        // haunted souls are spendable until a death takes them, so they are unspent souls too.
        var (number, below) = SoulsLine.Split(soulsCrop);
        // Each box with its own font: the clock and the souls count are drawn differently.
        var soulsText = await _engine.RecognizeAsync(number, OcrField.Souls, ct).ConfigureAwait(false);
        var clockText = await _engine.RecognizeAsync(timerCrop, OcrField.Clock, ct).ConfigureAwait(false);
        int haunted = below is null ? 0 : HudParser.ParseHaunted(await _engine.RecognizeWordsAsync(below, ct).ConfigureAwait(false)) ?? 0;
        if (haunted > 0 && LastHaunted == 0)
            _log.LogInformation("OCR: reading haunted souls as well ({Haunted:N0}), counted with your unspent souls", haunted);
        LastHaunted = haunted;
        int? mainSouls = HudParser.ParseSouls(soulsText);
        var souls = mainSouls is { } main ? main + haunted : (int?)null;
        var clock = HudParser.ParseClock(clockText);
        if (_watch.Record(souls.HasValue, clock.HasValue, now) is { } warning)
        {
            _log.LogWarning("OCR: {Warning}", warning);
            ReadsFailing?.Invoke(warning);
        }
        bool ok = souls.HasValue && clock.HasValue;
        // A misread clock would put the reading at the wrong game time everywhere downstream, so it is dropped, souls and all.
        // Until a first clock is confirmed (a few readings in a row) nothing is used, and that does not count against health.
        if (ok)
        {
            bool confirming = _clockGuard.Last is null;
            if (!_clockGuard.Accept(clock!.Value, now))
            {
                if (confirming) return null;
                ok = false;
                LogRejectedClock(clock.Value, now);
            }
        }
        _health.Record(now, ok);
        if (ok) Teach(number, soulsText, mainSouls!.Value, timerCrop, clockText, clock!.Value);
        return ok ? new SoulSample(clock!.Value, souls!.Value, SampleSource.Ocr, Haunted: haunted > 0 ? haunted : null) : null;
    }

    private (TimeSpan Clock, int Souls)? _lastTaught;

    /// <summary>
    /// A checked reading teaches the glyph reader the characters it does not know yet: the clock passed the clock guard;
    /// the souls count only when it moved plausibly from the reading before (not a purchase, a death or a teammate's
    /// counter), since a misread would teach the wrong shape.
    /// </summary>
    private void Teach(CapturedFrame number, string? soulsText, int souls, CapturedFrame timer, string? clockText, TimeSpan clock)
    {
        if (_engine is not GlyphOcrEngine glyphs) return;
        glyphs.LearnFromReading(OcrField.Clock, timer, clockText);
        if (_lastTaught is { } prev && OcrJumpGuard.Plausible(prev.Souls, souls, (clock - prev.Clock).TotalSeconds) && souls != prev.Souls)
            glyphs.LearnFromReading(OcrField.Souls, number, soulsText);
        _lastTaught = (clock, souls);
    }

    /// <summary>At most a line every 30 s, so a run of bad frames does not flood the log.</summary>
    private void LogRejectedClock(TimeSpan read, DateTimeOffset now)
    {
        if (now - _clockLogged < TimeSpan.FromSeconds(30)) return;
        int since = _clockGuard.Rejected - _clockLoggedRejected;
        _clockLogged = now;
        _clockLoggedRejected = _clockGuard.Rejected;
        _log.LogInformation("OCR: ignored {Count} clock readings that did not follow the game clock (read {Read}, {Expected} was due)",
            since, PaceMath.FormatClock(read), _clockGuard.Expected(now) is { } due ? PaceMath.FormatClock(due) : "no clock yet");
    }
}

/// <summary>Placeholders until M6: nothing is available, so the composite source always uses the live feed.</summary>
public sealed class NullRegionCapture : IRegionCapture
{
    public bool IsAvailable => false;
    public Task<CapturedFrame?> CaptureAsync(HudRegion region, CancellationToken ct) => Task.FromResult<CapturedFrame?>(null);
}

public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "none";
    public bool IsAvailable => false;
    public Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>The calibrated regions from settings, read live so a new calibration applies at once.</summary>
public sealed class SettingsHudRegionMap : IHudRegionMap
{
    private readonly Func<Core.Settings.HudRegions> _regions;
    public SettingsHudRegionMap(Func<Core.Settings.HudRegions> regions) => _regions = regions;
    public HudRegion? Souls => RegionCalibration.FromRect(_regions().Souls);
    public HudRegion? Timer => RegionCalibration.FromRect(_regions().Timer);
    public IReadOnlyList<HudRegion?> EnemySouls => _regions().EnemySouls.Select(RegionCalibration.FromRect).ToList();
}
