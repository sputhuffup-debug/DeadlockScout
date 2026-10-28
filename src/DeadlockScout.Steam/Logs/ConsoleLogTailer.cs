using System.Runtime.CompilerServices;
using DeadlockScout.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockScout.Steam.Logs;

/// <summary>Primary match signal source: the game's own console.log (needs -condebug).</summary>
public sealed class ConsoleLogTailer : IMatchSignalSource
{
    public const string SourceName = "console.log";

    private readonly Func<string?> _pathProvider;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly bool _fromStart;
    private readonly TimeSpan _poll;

    /// <param name="pathProvider">Resolves the log path each time the tailer (re)starts, so a late-found install works.</param>
    public ConsoleLogTailer(Func<string?> pathProvider, IClock clock, ILogger<ConsoleLogTailer>? log = null,
                            bool fromStart = false, TimeSpan? poll = null)
    {
        _pathProvider = pathProvider;
        _clock = clock;
        _log = log ?? NullLogger<ConsoleLogTailer>.Instance;
        _fromStart = fromStart;
        _poll = poll ?? LogTailer.DefaultPoll;
    }

    public string Name => SourceName;

    public async IAsyncEnumerable<MatchSignal> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var path = _pathProvider();
            if (path is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }
            var tailer = new LogTailer(path, _poll, _fromStart);
            await foreach (var line in tailer.ReadLinesAsync(ct).ConfigureAwait(false))
            {
                var signal = Parse(line, _clock.UtcNow);
                if (signal is not null) yield return signal;
            }
        }
    }

    /// <summary>Turns one console line into a signal, or null. Pure, so fixtures can be replayed.</summary>
    public static MatchSignal? Parse(string rawLine, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;
        var line = ConsoleLogPatterns.StripTimestamp(rawLine);

        var m = ConsoleLogPatterns.LobbyCreated().Match(line);
        if (m.Success && long.TryParse(m.Groups["lobby"].Value, out var lobby) && long.TryParse(m.Groups["match"].Value, out var match))
            return new MatchSignal.LobbyCreated(at, SourceName, lobby, match);

        m = ConsoleLogPatterns.ServerTicket().Match(line);
        if (m.Success && long.TryParse(m.Groups["match"].Value, out var ticketed))
            return new MatchSignal.Started(at, SourceName, ticketed);

        m = ConsoleLogPatterns.HeroSpawn().Match(line);
        if (m.Success) return new MatchSignal.HeroLocked(at, SourceName, m.Groups["hero"].Value.ToLowerInvariant());

        m = ConsoleLogPatterns.GameState().Match(line);
        if (m.Success && int.TryParse(m.Groups["state"].Value, out var state) && state >= ConsoleLogPatterns.FirstPostGameState)
            return new MatchSignal.Ended(at, SourceName, 0);

        m = ConsoleLogPatterns.Disconnected().Match(line);
        if (m.Success && m.Groups["reason"].Value != ConsoleLogPatterns.LoopSwitchReason)
            return new MatchSignal.Ended(at, SourceName, 0);

        if (ConsoleLogPatterns.HideoutLoading().IsMatch(line))
            return new MatchSignal.Ended(at, SourceName, 0);

        return null;
    }
}
