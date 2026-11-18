namespace DeadlockScout.Core.Diagnostics;

/// <summary>
/// What the tray says when the app hits an error nothing caught (0.9): every one goes to the log, but the tray line comes
/// at most once per <see cref="Quiet"/>, counting the ones in between, so a fault that repeats every second does not
/// bury the screen in notices. Pure, so tested.
/// </summary>
public sealed class ErrorNotice
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    private const int MaxMessage = 140;

    private DateTimeOffset? _lastShown;
    private int _sinceShown;

    /// <summary>Errors seen since the app started.</summary>
    public int Total { get; private set; }

    /// <summary>Counts <paramref name="error"/>; returns the tray line to show now, or null while the last one is recent.</summary>
    public string? Seen(Exception error, DateTimeOffset now)
    {
        Total++;
        _sinceShown++;
        if (_lastShown is { } last && now - last < Quiet) return null;
        int count = _sinceShown;
        _sinceShown = 0;
        _lastShown = now;
        return (count == 1 ? Describe(error) : $"{count} errors since the last notice, the latest: {Describe(error)}") +
               ". It is in the log and the app keeps running; click to open the logs folder.";
    }

    /// <summary>"InvalidOperationException: Sequence contains no elements", the innermost error, cut to fit a notice.</summary>
    public static string Describe(Exception error)
    {
        var e = error is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : error.GetBaseException();
        var message = e.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > MaxMessage) message = message[..(MaxMessage - 1)] + "…";
        return message.Length == 0 ? e.GetType().Name : $"{e.GetType().Name}: {message}";
    }
}
