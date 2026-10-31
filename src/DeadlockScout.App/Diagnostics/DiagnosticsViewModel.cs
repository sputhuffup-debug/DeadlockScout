using System.Collections.ObjectModel;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Diagnostics;
using DeadlockScout.Core.Laning;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Diagnostics;

/// <summary>One "label: value" line on the diagnostics page.</summary>
public sealed record DiagnosticsLine(string Label, string Value, bool Warn = false);

/// <summary>One endpoint family's row in the API table.</summary>
public sealed record ApiRow(string Family, string Calls, string Errors, string TooMany, string NotFound, string Average, string Waits, bool Warn);

/// <summary>
/// The diagnostics page: setup (moved here from the main window), the last match (feed, roster, broadcast delay, clock),
/// what the app is using right now (clock, souls source, OCR), and every API call since it started. Rebuilt from a
/// <see cref="DiagnosticsLog"/> snapshot once a second while the page is open; nothing here is stored.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsLog _log;
    private readonly OverlayState _state;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly OcrSoulSource? _ocr;
    private readonly OcrJumpGuard? _guard;
    private readonly Func<uint> _accountId;
    private readonly Func<LiveDiagnostics>? _liveOverride;
    private string _matchTitle = "", _since = "", _copyStatus = "";

    public DiagnosticsViewModel(DiagnosticsLog log, OverlayState state, IOptionsMonitor<ScoutSettings> settings, Func<uint> accountId,
                                OcrSoulSource? ocr = null, OcrJumpGuard? guard = null, Func<LiveDiagnostics>? liveOverride = null)
    {
        _log = log;
        _state = state;
        _settings = settings;
        _accountId = accountId;
        _ocr = ocr;
        _guard = guard;
        _liveOverride = liveOverride;
        Refresh();
    }

    /// <summary>The main window's status: the phase and the setup lines that used to sit at its top.</summary>
    public StatusViewModel Status => _state.Status;
    public ObservableCollection<DiagnosticsLine> Match { get; } = new();
    public ObservableCollection<DiagnosticsLine> Now { get; } = new();
    public ObservableCollection<ApiRow> Api { get; } = new();
    public ObservableCollection<DiagnosticsLine> Errors { get; } = new();
    public string MatchTitle { get => _matchTitle; private set => Set(ref _matchTitle, value); }
    public string Since { get => _since; private set => Set(ref _since, value); }
    public string CopyStatus { get => _copyStatus; set => Set(ref _copyStatus, value); }

    public static string AppVersion =>
        typeof(DiagnosticsViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    public LiveDiagnostics Live()
    {
        if (_liveOverride is { } demo) return demo();
        var s = _settings.CurrentValue;
        var clock = _state.InMatch ? _state.Clock.Read(DateTimeOffset.UtcNow, _state.AssumedDelay)?.Source : null;
        return new LiveDiagnostics(AppVersion, s.Api.LiveSource.ToString(), s.Ocr.Enabled,
            clock switch
            {
                ClockSource.Screen => "HUD clock (OCR)",
                ClockSource.GameLog => "game log's start line",
                ClockSource.Broadcast => $"broadcast + {_state.AssumedDelay.TotalSeconds:0} s assumed",
                _ => null,
            },
            _state.InMatch ? _state.Pace.SourceLabel : null,
            s.Ocr.Enabled && _ocr is not null && !_state.Spectating ? _ocr.SuccessRate : null, // spectating, your souls are not on screen
            _guard?.Dropped ?? 0,
            _state.InMatch);
    }

    public void Refresh()
    {
        var snap = _log.Snapshot();
        var live = Live();
        Since = $"since {snap.Since.ToLocalTime():HH:mm}";

        Match.Clear();
        if (snap.Match is not { } m)
        {
            MatchTitle = "No match since the app started";
        }
        else
        {
            MatchTitle = $"{(live.InMatch ? "This match" : "Last match")} · {(m.Spectating ? "spectated · " : "")}{m.StartedAt.ToLocalTime():HH:mm}" +
                         (m.EndedAt is { } end ? $" · {DiagnosticsReport.Duration(end - m.StartedAt)}" : " · live");
            Match.Add(new("Match", m.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Match.Add(new("Live feed", m.Feed ?? "none yet", m.Feed is null));
            Match.Add(new("Roster", m.RosterAfter is { } r ? $"after {DiagnosticsReport.Duration(r)}" : "not received", m.RosterAfter is null));
            Match.Add(new("Broadcast delay", m.Delay is { } d ? $"{d.TotalSeconds:0.0} s, {m.DelayHow}" : "not measured"));
            if (m.ClockOffset is { } o)
                Match.Add(new("Game log vs HUD clock", $"{o.TotalSeconds:+0.0;-0.0} s", Math.Abs(o.TotalSeconds) > 3));
            foreach (var n in m.Notes) Match.Add(new("Note", n, true));
        }

        Now.Clear();
        Now.Add(new("Clock", live.ClockSource ?? (live.InMatch ? "none yet" : "– (no match)")));
        Now.Add(new("Souls from", live.SoulsSource ?? "– (no match)"));
        Now.Add(new("OCR", !live.OcrEnabled ? "off" : live.InMatch && live.OcrReadRate is { } rate ? $"on, reading {rate:P0} of the time" : "on"));
        if (live.OcrDropped > 0) Now.Add(new("OCR readings dropped", $"{live.OcrDropped} (a teammate's souls, read while you were dead)"));

        Api.Clear();
        foreach (var f in snap.Api)
            Api.Add(new ApiRow(f.Family, f.Attempts.ToString(), f.Errors.ToString(), f.TooMany.ToString(), f.NotFound.ToString(),
                $"{f.AverageTime.TotalMilliseconds:0} ms",
                f.Waits == 0 ? "–" : $"{f.Waits}× · {DiagnosticsReport.Duration(f.Waited)} (max {DiagnosticsReport.Duration(f.LongestWait)})",
                f.Errors > 0 || f.TooMany > 0));

        Errors.Clear();
        foreach (var e in snap.Errors)
            Errors.Add(new(e.At.ToLocalTime().ToString("HH:mm:ss"), $"{e.Family} {e.Path} {(e.Status is { } st ? st.ToString() : "")} {e.Error}".Trim(), true));
    }

    /// <summary>The text "Copy diagnostics" puts on the clipboard: the report and the setup lines, with every id and name taken out.</summary>
    public string Report()
    {
        var text = DiagnosticsReport.Format(_log.Snapshot(), Live(), DateTimeOffset.UtcNow)
                   + Environment.NewLine + "Setup" + Environment.NewLine
                   + string.Join(Environment.NewLine, Status.Detection.Split('\n').Select(l => "  " + l.TrimEnd('\r')))
                   + (Status.Warning is { Length: > 0 } w ? Environment.NewLine + "  warning: " + w : "");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        text = DiagnosticsReport.HideProfile(text, profile);
        uint me = _accountId();
        var secrets = new List<string?>
        {
            me == 0 ? null : me.ToString(System.Globalization.CultureInfo.InvariantCulture),
            me == 0 ? null : (76561197960265728UL + me).ToString(System.Globalization.CultureInfo.InvariantCulture), // the SteamID64
            _settings.CurrentValue.Api.ApiKey,
            Environment.UserName,
            // The profile folder's own name, which can differ from the user name, wherever a path outside the profile
            // still carries it (a settings path pointed elsewhere, a log line).
            string.IsNullOrEmpty(profile) ? null : System.IO.Path.GetFileName(profile.TrimEnd('\\', '/')),
        };
        secrets.AddRange(_state.RosterSecrets());
        return DiagnosticsReport.Scrub(text, secrets);
    }
}
