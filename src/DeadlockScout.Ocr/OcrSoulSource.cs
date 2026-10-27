using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;
using DeadlockScout.Ocr.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>
/// Reads your soul counter and the match clock off the screen at ~2 Hz. Real time, so it is the source that removes the
/// broadcast delay once an engine is available. Emits nothing while unhealthy, and the composite source then falls
/// back to the live feed.
/// </summary>
public sealed class OcrSoulSource : ISoulSampleSource
{
    private readonly ICaptureSource _capture;
    private readonly IHudRegionMap _regions;
    private readonly IOcrEngine _engine;
    private readonly IClock _clock;
    private readonly OcrHealth _health;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;

    public OcrSoulSource(ICaptureSource capture, IHudRegionMap regions, IOcrEngine engine, IClock clock,
                         double minHealthy = 0.8, TimeSpan? interval = null, ILogger<OcrSoulSource>? log = null)
    {
        _capture = capture;
        _regions = regions;
        _engine = engine;
        _clock = clock;
        _health = new OcrHealth(minHealthy: minHealthy);
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
        _log = log ?? NullLogger<OcrSoulSource>.Instance;
    }

    public SampleSource Kind => SampleSource.Ocr;
    public TimeSpan ExpectedLatency => TimeSpan.FromSeconds(1);
    public bool IsHealthy => _engine.IsAvailable && _capture.IsAvailable && _health.IsHealthy;
    public double SuccessRate => _health.SuccessRate;

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_engine.IsAvailable || !_capture.IsAvailable)
        {
            _log.LogInformation("OCR source idle: engine {Engine} available={EngineOk}, capture available={CaptureOk}", _engine.Name, _engine.IsAvailable, _capture.IsAvailable);
            yield break;
        }
        while (!ct.IsCancellationRequested)
        {
            var sample = await TryReadOnceAsync(ct).ConfigureAwait(false);
            if (sample is not null) yield return sample;
            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One capture + two recognitions; records health. Exposed for tests with fake engines.</summary>
    public async Task<SoulSample?> TryReadOnceAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_regions.Souls is null || _regions.Timer is null) { _health.Record(now, false); return null; }
        var frame = await _capture.CaptureAsync(ct).ConfigureAwait(false);
        if (frame is null) { _health.Record(now, false); return null; }

        var soulsText = await _engine.RecognizeAsync(frame, _regions.Souls, ct).ConfigureAwait(false);
        var clockText = await _engine.RecognizeAsync(frame, _regions.Timer, ct).ConfigureAwait(false);
        var souls = HudParser.ParseSouls(soulsText);
        var clock = HudParser.ParseClock(clockText);
        bool ok = souls.HasValue && clock.HasValue;
        _health.Record(now, ok);
        return ok ? new SoulSample(clock!.Value, souls!.Value, SampleSource.Ocr) : null;
    }
}

/// <summary>Placeholders until M6: nothing is available, so the composite source always uses the live feed.</summary>
public sealed class NullCaptureSource : ICaptureSource
{
    public bool IsAvailable => false;
    public Task<CapturedFrame?> CaptureAsync(CancellationToken ct) => Task.FromResult<CapturedFrame?>(null);
}

public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "none";
    public bool IsAvailable => false;
    public Task<string?> RecognizeAsync(CapturedFrame frame, HudRegion region, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Regions from settings (1080p-relative rectangles converted to normalised ones).</summary>
public sealed class SettingsHudRegionMap : IHudRegionMap
{
    private readonly Func<Core.Settings.HudRegions> _regions;
    public SettingsHudRegionMap(Func<Core.Settings.HudRegions> regions) => _regions = regions;
    public HudRegion? Souls => Convert(_regions().Souls);
    public HudRegion? Timer => Convert(_regions().Timer);
    private static HudRegion? Convert(Core.Settings.HudRect? r) => r is null ? null : new HudRegion(r.X / 1920.0, r.Y / 1080.0, r.Width / 1920.0, r.Height / 1080.0);
}
