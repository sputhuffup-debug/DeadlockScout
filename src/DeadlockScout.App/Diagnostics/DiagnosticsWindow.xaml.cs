using System.Windows;
using System.Windows.Threading;

namespace DeadlockScout.App.Diagnostics;

/// <summary>
/// The diagnostics page (M8): setup, the last match's feed, roster, broadcast delay and clock, what the app uses right
/// now, and every API call since it started, with "Copy diagnostics" for a bug report. Refreshes once a second while
/// open, and the setup lines every five.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _vm;
    private readonly DispatcherTimer _timer;
    private int _ticks;

    public DiagnosticsWindow(DiagnosticsViewModel vm, Action? refreshSetup = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        refreshSetup?.Invoke();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _vm.Refresh();
            if (++_ticks % 5 == 0) refreshSetup?.Invoke();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>The scrolling page, for the screenshot renderer.</summary>
    public FrameworkElement BodyPart => Body;

    /// <summary>
    /// A made-up log for the screenshots: a 35:51 match on the Direct feed whose broadcast took a minute to start, and a
    /// spread of API results (404s from a match not processed yet, a 429, a Steam-busy 503, a few rate-limit waits).
    /// </summary>
    public static Core.Diagnostics.DiagnosticsLog DemoLog(DateTimeOffset now)
    {
        var log = new Core.Diagnostics.DiagnosticsLog(now - TimeSpan.FromHours(2));
        var start = now - TimeSpan.FromMinutes(40);
        log.MatchStarted(123456789, false, start);
        log.Feed("Direct broadcast");
        log.FeedNote("Broadcast said \"not yet\" 6× and started after 61 s");
        log.RosterReceived(start + TimeSpan.FromSeconds(64));
        log.BroadcastDelay(TimeSpan.FromSeconds(34.1), "against the game log's start line");
        log.ClockCheck(TimeSpan.FromSeconds(0.4));
        log.MatchEnded(start + new TimeSpan(0, 35, 51));
        void Calls(string family, string path, int n, int ms, int? status = 200)
        {
            for (int i = 0; i < n; i++) log.ApiAttempt(family, path, status, null, TimeSpan.FromMilliseconds(ms + i * 7 % 90), start + TimeSpan.FromSeconds(i));
        }
        Calls("Analytics", "/v1/analytics/item-stats", 41, 820);
        Calls("HeroStats", "/v1/players/hero-stats", 2, 330);
        Calls("Default", "/v1/players/1000001/mate-stats", 12, 260);
        Calls("Default", "/v1/players/rank", 2, 250);
        Calls("Metadata", "/v1/matches/metadata", 4, 600);
        Calls("Metadata", "/v1/matches/metadata", 3, 360, 404);
        Calls("LiveUrl", "/v1/matches/123456789/live/url", 1, 910);
        Calls("Assets", "/assets-api-res/images/upgrades/mods_tech/escalating_exposure.png", 3, 420);
        Calls("MatchHistory", "/v1/players/1000001/match-history", 2, 230);
        log.ApiAttempt("Analytics", "/v1/analytics/item-stats", null, "timed out", TimeSpan.FromSeconds(30), now - TimeSpan.FromMinutes(31));
        log.ApiAttempt("MatchHistory", "/v1/players/1000001/match-history", 429, null, TimeSpan.FromMilliseconds(120), now - TimeSpan.FromMinutes(3));
        log.ApiAttempt("Metadata", "/v1/matches/123456789/metadata", 503, null, TimeSpan.FromMilliseconds(4100), now - TimeSpan.FromMinutes(2));
        log.RateLimitWait("Default", TimeSpan.FromSeconds(2.1));
        log.RateLimitWait("Default", TimeSpan.FromSeconds(4.4));
        log.RateLimitWait("Analytics", TimeSpan.FromSeconds(1.1));
        return log;
    }

    /// <summary>The "right now" values the screenshot shows: OCR on, and the teammate readings it dropped last match.</summary>
    public static Core.Diagnostics.LiveDiagnostics DemoLive => new(DiagnosticsViewModel.AppVersion, "Direct", true, null, null, 0.97, 23, false);

    private void Copy(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_vm.Report());
            _vm.CopyStatus = "Copied, without account ids, player names, your Windows user name or the API key.";
        }
        catch (Exception ex)
        {
            _vm.CopyStatus = $"Could not copy: {ex.Message}";
        }
    }
}
