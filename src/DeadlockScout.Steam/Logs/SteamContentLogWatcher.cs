using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DeadlockScout.Core.Abstractions;

namespace DeadlockScout.Steam.Logs;

/// <summary>
/// Fallback match signal source: Steam's content_log.txt notes when it pauses and resumes updates for a running app,
/// which brackets a match. It carries no match id, so the composite detector only uses it when console.log is silent.
/// Line shapes are UNVERIFIED against the current Steam client; see ConsoleLogPatterns for the policy.
/// </summary>
public sealed partial class SteamContentLogWatcher : IMatchSignalSource
{
    public const string SourceName = "content_log";

    private readonly Func<string?> _pathProvider;
    private readonly IClock _clock;
    private readonly bool _fromStart;
    private readonly TimeSpan _poll;

    public SteamContentLogWatcher(Func<string?> pathProvider, IClock clock, bool fromStart = false, TimeSpan? poll = null)
    {
        _pathProvider = pathProvider;
        _clock = clock;
        _fromStart = fromStart;
        _poll = poll ?? TimeSpan.FromSeconds(1);
    }

    public string Name => SourceName;

    /// <summary>"[2026-10-26 20:01:02] AppID 1422450 ... updates disabled" / "updates enabled". Groups: app, state.</summary>
    [GeneratedRegex(@"AppID\s+(?<app>\d+).*?\bupdates?\s+(?<state>disabled|enabled)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdatesToggled();

    /// <summary>"AppID 1422450 state changed : Fully Installed,App Running," — the app launched or quit. Groups: app, states.</summary>
    [GeneratedRegex(@"AppID\s+(?<app>\d+)\s+state changed\s*:\s*(?<states>[^\r\n]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateChanged();

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
            bool running = false;
            await foreach (var line in tailer.ReadLinesAsync(ct).ConfigureAwait(false))
            {
                var signal = Parse(line, _clock.UtcNow, ref running);
                if (signal is not null) yield return signal;
            }
        }
    }

    /// <summary>Pure parser. <paramref name="running"/> tracks whether the app is up so a quit ends the match too.</summary>
    public static MatchSignal? Parse(string line, DateTimeOffset at, ref bool running)
    {
        var m = UpdatesToggled().Match(line);
        if (m.Success && m.Groups["app"].Value == SteamLocator.DeadlockAppId.ToString())
        {
            return m.Groups["state"].Value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? new MatchSignal.Started(at, SourceName, 0)
                : new MatchSignal.Ended(at, SourceName, 0);
        }
        m = StateChanged().Match(line);
        if (m.Success && m.Groups["app"].Value == SteamLocator.DeadlockAppId.ToString())
        {
            bool nowRunning = m.Groups["states"].Value.Contains("App Running", StringComparison.OrdinalIgnoreCase);
            if (running && !nowRunning)
            {
                running = false;
                return new MatchSignal.Ended(at, SourceName, 0);
            }
            running = nowRunning;
        }
        return null;
    }
}
