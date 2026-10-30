using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Sources;

/// <summary>
/// Honours <see cref="SoulSourceMode"/>: in Auto it forwards OCR samples while the OCR source is enabled and healthy,
/// otherwise the live feed's. Both sources run; only the forwarding changes, so the meter never resets. OCR readings pass
/// through an <see cref="OcrJumpGuard"/>, fed with your broadcast souls, so a teammate's counter read while you are dead
/// never reaches the meter.
/// </summary>
public sealed class CompositeSoulSource : ISoulSampleSource
{
    private readonly ISoulSampleSource _feed;
    private readonly ISoulSampleSource? _ocr;
    private readonly Func<SoulSourceMode> _mode;
    private readonly Func<bool> _ocrEnabled;
    private readonly OcrJumpGuard _guard;
    private volatile ISoulSampleSource _active;

    public CompositeSoulSource(ISoulSampleSource feed, ISoulSampleSource? ocr, Func<SoulSourceMode> mode, Func<bool> ocrEnabled, OcrJumpGuard? guard = null)
    {
        _feed = feed;
        _ocr = ocr;
        _mode = mode;
        _ocrEnabled = ocrEnabled;
        _guard = guard ?? new OcrJumpGuard();
        _active = feed;
    }

    public SampleSource Kind => _active.Kind;
    public TimeSpan ExpectedLatency => _active.ExpectedLatency;
    public bool IsHealthy => _active.IsHealthy;
    public ISoulSampleSource Active => _active;

    /// <summary>Which source should be forwarded right now.</summary>
    public ISoulSampleSource Choose()
    {
        var mode = _mode();
        if (_ocr is null) return _feed;
        return mode switch
        {
            SoulSourceMode.LiveFeed => _feed,
            SoulSourceMode.Ocr => _ocr,
            _ => _ocrEnabled() && _ocr.IsHealthy ? _ocr : _feed,
        };
    }

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SoulSample>(new UnboundedChannelOptions { SingleReader = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pumps = new List<Task> { Pump(_feed, ctx, channel.Writer, cts.Token) };
        if (_ocr is not null) pumps.Add(Pump(_ocr, ctx, channel.Writer, cts.Token));
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        _guard.Reset();
        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var chosen = Choose();
                _active = chosen;
                // Every broadcast sample goes to the guard, forwarded or not: it settles the OCR readings held back.
                var released = sample.Source == SampleSource.Ocr ? _guard.OfferOcr(sample) : _guard.OfferFeed(sample);
                if (chosen.Kind == SampleSource.Ocr)
                {
                    foreach (var r in released) yield return r;
                }
                else if (sample.Source == chosen.Kind)
                {
                    yield return sample;
                }
            }
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static async Task Pump(ISoulSampleSource source, MatchContext ctx, ChannelWriter<SoulSample> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var s in source.ReadAsync(ctx, ct).ConfigureAwait(false))
                await writer.WriteAsync(s, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
