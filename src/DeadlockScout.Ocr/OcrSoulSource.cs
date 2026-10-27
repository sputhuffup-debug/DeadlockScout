using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
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

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // Only a missing engine ends the source. Capture comes and goes (the game not in front, minimized, not started yet)
        // and is checked on every pass, so being alt-tabbed when the match starts does not switch OCR off for the match.
        if (!_engine.IsAvailable)
        {
            _log.LogInformation("OCR source idle: engine {Engine} is not available", _engine.Name);
            yield break;
        }
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

        var soulsText = await _engine.RecognizeAsync(soulsCrop, ct).ConfigureAwait(false);
        var clockText = await _engine.RecognizeAsync(timerCrop, ct).ConfigureAwait(false);
        var souls = HudParser.ParseSouls(soulsText);
        var clock = HudParser.ParseClock(clockText);
        bool ok = souls.HasValue && clock.HasValue;
        _health.Record(now, ok);
        return ok ? new SoulSample(clock!.Value, souls!.Value, SampleSource.Ocr) : null;
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
