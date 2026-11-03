using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Api.Live;

/// <summary>
/// Your own net-worth ticks from the feed as soul samples, stamped with the feed's game clock, with your unspent souls
/// (net worth less the cost of the items the tick says you hold) when the prices are known.
/// </summary>
public sealed class LiveFeedSoulSource : ISoulSampleSource
{
    private readonly ILiveMatchFeed _feed;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;

    private readonly Func<IReadOnlyList<long>?, int?>? _heldCost;

    /// <param name="heldCost">The cost of the items held, by item id; null when the prices are not known.</param>
    public LiveFeedSoulSource(ILiveMatchFeed feed, Func<IReadOnlyList<long>?, int?>? heldCost = null)
    {
        _feed = feed;
        _heldCost = heldCost;
    }

    public SampleSource Kind => SampleSource.LiveFeed;
    /// <summary>Valve's spectator broadcast runs about this far behind the live game.</summary>
    public TimeSpan ExpectedLatency => TimeSpan.FromSeconds(45); // measured 34–55 s behind the game
    public bool IsHealthy => DateTimeOffset.UtcNow - _lastSample < TimeSpan.FromSeconds(30);

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tick in _feed.TicksAsync(ctx, ct).ConfigureAwait(false))
        {
            if (tick.AccountId != ctx.MyAccountId) continue;
            _lastSample = DateTimeOffset.UtcNow;
            int? held = _heldCost?.Invoke(tick.Items);
            yield return new SoulSample(tick.GameTime, tick.NetWorth, SampleSource.LiveFeed, held is { } h ? tick.NetWorth - h : null);
        }
    }
}
