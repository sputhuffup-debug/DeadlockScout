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
            long? startAt = null;
            if (!_fromStart)
            {
                // Started mid-match: the lobby line is already in the file, so pick the match up from what is there.
                var (lines, end) = ReadTail(path);
                startAt = end;
                IReadOnlyList<MatchSignal> resume = Array.Empty<MatchSignal>();
                try { resume = ResumeSignals(lines, _clock.UtcNow, _clock.UtcNow.ToLocalTime().DateTime); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not scan console.log for a match in progress; following new lines only"); }
                if (resume.Count > 0)
                    _log.LogInformation("console.log shows a match in progress; resuming it");
                foreach (var s in resume) yield return s;
            }
            var tailer = new LogTailer(path, _poll, _fromStart, startAt);
            await foreach (var line in tailer.ReadLinesAsync(ct).ConfigureAwait(false))
            {
                var signal = Parse(line, _clock.UtcNow);
                if (signal is not null) yield return signal;
            }
        }
    }

    /// <summary>
    /// How long the game can go without writing a line while it runs. It logs a frame-time line every minute even when
    /// idle (the longest silence in a real session was 61 s), so a log quiet for longer is from a game that has closed
    /// or crashed, and its unfinished match is not resumed.
    /// </summary>
    public static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(3);

    /// <summary>The most of the file the startup scan reads: a whole session's log was about 1.5 MB.</summary>
    public const int MaxScanBytes = 16 * 1024 * 1024;

    /// <summary>
    /// What an existing console.log says about a match still in progress, for an app started mid-match: the start
    /// signals (and hero) of the last match that has no end line, or nothing when the log ends outside a match or has
    /// been quiet longer than <see cref="MaxSilence"/>. Pure, so fixtures can be replayed.
    /// </summary>
    public static IReadOnlyList<MatchSignal> ResumeSignals(IEnumerable<string> lines, DateTimeOffset at, DateTime nowLocal)
    {
        List<MatchSignal>? open = null;
        long openMatch = 0;
        DateTime? last = null;
        foreach (var line in lines)
        {
            if (ConsoleLogPatterns.LineTimeOf(line, nowLocal) is { } t) last = t;
            switch (Parse(line, at))
            {
                case MatchSignal.LobbyCreated lc:
                    if (lc.MatchId != openMatch) { open = new(); openMatch = lc.MatchId; }
                    open!.Add(lc);
                    break;
                case MatchSignal.Started st when st.MatchId > 0:
                    if (st.MatchId != openMatch) { open = new(); openMatch = st.MatchId; }
                    open!.Add(st);
                    break;
                case MatchSignal.Spectating sp when sp.MatchId > 0:
                    if (sp.MatchId != openMatch) { open = new(); openMatch = sp.MatchId; }
                    open!.Add(sp);
                    break;
                case MatchSignal.Ended en when en.MatchId == 0 || en.MatchId == openMatch:
                    open = null; openMatch = 0;
                    break;
                case MatchSignal.HeroLocked hl:
                    open?.Add(hl);
                    break;
            }
        }
        if (open is null || last is null || nowLocal - last.Value > MaxSilence) return Array.Empty<MatchSignal>();
        return open;
    }

    /// <summary>The complete lines of the last <see cref="MaxScanBytes"/> of the file, and the offset the read stopped at (null if it could not be read).</summary>
    private static (List<string> Lines, long? End) ReadTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long end = fs.Length;
            long start = Math.Max(0, end - MaxScanBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[end - start];
            int read = 0;
            while (read < bytes.Length && fs.Read(bytes, read, bytes.Length - read) is > 0 and var n) read += n;
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, read);
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return (new List<string>(), start);  // no complete line yet; the tailer reads it all
            var complete = text[..(lastNewline + 1)];
            var lines = complete.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // the first line was cut by the seek
            // Tail from just after the last complete line, so a half-written one is read whole later.
            return (lines, start + System.Text.Encoding.UTF8.GetByteCount(complete));
        }
        catch (IOException) { return (new List<string>(), null); } // the tailer starts at the end, as before
        catch (UnauthorizedAccessException) { return (new List<string>(), null); }
    }

    /// <summary>Turns one console line into a signal, or null. Pure, so fixtures can be replayed.</summary>
    public static MatchSignal? Parse(string rawLine, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;
        var line = ConsoleLogPatterns.StripTimestamp(rawLine);

        var m = ConsoleLogPatterns.LobbyCreated().Match(line);
        if (m.Success && long.TryParse(m.Groups["lobby"].Value, out var lobby) && long.TryParse(m.Groups["match"].Value, out var match))
            return new MatchSignal.LobbyCreated(at, SourceName, lobby, match);

        m = ConsoleLogPatterns.LobbyDestroyed().Match(line);
        if (m.Success && long.TryParse(m.Groups["match"].Value, out var destroyed))
            return new MatchSignal.Ended(at, SourceName, destroyed);

        m = ConsoleLogPatterns.BroadcastPlaying().Match(line);
        if (m.Success && long.TryParse(m.Groups["match"].Value, out var watched))
            return new MatchSignal.Spectating(at, SourceName, watched);

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
