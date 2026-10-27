using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using DeadlockScout.Core.Models;

namespace DeadlockScout.Api.Live;

/// <summary>Your own net-worth ticks from the feed as soul samples, stamped with the feed's game clock.</summary>
public sealed class LiveFeedSoulSource : ISoulSampleSource
{
    private readonly ILiveMatchFeed _feed;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;

    public LiveFeedSoulSource(ILiveMatchFeed feed) => _feed = feed;

    public SampleSource Kind => SampleSource.LiveFeed;
    /// <summary>Valve's spectator broadcast runs about this far behind the live game.</summary>
    public TimeSpan ExpectedLatency => TimeSpan.FromSeconds(75);
    public bool IsHealthy => DateTimeOffset.UtcNow - _lastSample < TimeSpan.FromSeconds(30);

    public async IAsyncEnumerable<SoulSample> ReadAsync(MatchContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tick in _feed.TicksAsync(ctx, ct).ConfigureAwait(false))
        {
            if (tick.AccountId != ctx.MyAccountId) continue;
            _lastSample = DateTimeOffset.UtcNow;
            yield return new SoulSample(tick.GameTime, tick.NetWorth, SampleSource.LiveFeed);
        }
    }
}
